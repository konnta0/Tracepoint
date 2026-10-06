using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tracepoint.Weaving;

internal static class MethodWeaver
{
    private const string ActivityFieldName = "<Tracepoint>activity";

    internal static void Weave(MethodDefinition method, WeaveContext context, List<WeaveDiagnostic> diagnostics)
    {
        if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl || method.IsInternalCall)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "the method has no body")));
            return;
        }

        if (method.Name.Contains('<') || (method.DeclaringType.Name.Contains('<') && !method.DeclaringType.Name.Contains("d__")))
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "local functions are not supported")));
            return;
        }

        if (method.HasGenericParameters)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "generic methods are not supported")));
            return;
        }

        if (method.ReturnType.IsByReference)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "ref returns are not supported")));
            return;
        }

        if (AssemblyWeaver.HasAttribute(method, "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "iterators are not supported")));
            return;
        }

        var asyncAttribute = GetAttribute(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        if (asyncAttribute is not null && method.ReturnType.MetadataType == MetadataType.Void)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "async void is not supported")));
            return;
        }

        if (!TryGetSpanName(method, out var spanName, out var nameDiagnostic))
        {
            diagnostics.Add(nameDiagnostic);
            return;
        }

        if (!TryGetTags(method, diagnostics, out var tags))
        {
            return;
        }

        if (asyncAttribute is not null)
        {
            WeaveAsync(method, context, spanName, tags, diagnostics);
            return;
        }

        WeaveSync(method, context, spanName, tags);
    }

    private static void WeaveSync(MethodDefinition method, WeaveContext context, string spanName, List<TagSite> tags)
    {
        var body = method.Body;
        ExpandBranches(body);
        var originalInstructions = body.Instructions.ToArray();
        var handlerSnapshots = SnapshotHandlers(body);
        var fastPath = CloneInstructions(originalInstructions);
        var il = body.GetILProcessor();
        body.InitLocals = true;

        var activity = new VariableDefinition(context.ActivityType);
        var exception = new VariableDefinition(context.ExceptionType);
        body.Variables.Add(activity);
        body.Variables.Add(exception);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            result = new VariableDefinition(method.ReturnType);
            body.Variables.Add(result);
        }

        var afterHandlers = il.Create(OpCodes.Nop);
        foreach (var instruction in body.Instructions.ToArray())
        {
            if (instruction.OpCode != OpCodes.Ret)
            {
                continue;
            }

            if (instruction.Previous?.OpCode == OpCodes.Tail)
            {
                il.Remove(instruction.Previous);
            }

            if (result is not null)
            {
                var store = il.Create(OpCodes.Stloc, result);
                il.InsertBefore(instruction, store);
                var leave = il.Create(OpCodes.Leave, afterHandlers);
                Retarget(body, instruction, leave);
                il.Replace(instruction, leave);
            }
            else
            {
                var leave = il.Create(OpCodes.Leave, afterHandlers);
                Retarget(body, instruction, leave);
                il.Replace(instruction, leave);
            }
        }

        var anchor = body.Instructions[0];

        var prologue = new List<Instruction>();
        prologue.Add(il.Create(OpCodes.Ldsfld, context.SourceField));
        prologue.Add(il.Create(OpCodes.Ldstr, spanName));
        prologue.Add(il.Create(OpCodes.Ldc_I4_0));
        prologue.Add(il.Create(OpCodes.Callvirt, context.StartActivity));
        prologue.Add(il.Create(OpCodes.Stloc, activity));
        AppendTags(il, prologue, tags, context, instructions => instructions.Add(il.Create(OpCodes.Ldloc, activity)));
        InsertBefore(il, anchor, prologue);

        var catchStart = il.Create(OpCodes.Nop);
        var rethrow = il.Create(OpCodes.Rethrow);
        var finallyStart = il.Create(OpCodes.Nop);
        var endFinally = il.Create(OpCodes.Endfinally);
        var skipDispose = il.Create(OpCodes.Nop);
        var skipRecord = il.Create(OpCodes.Nop);

        il.Append(catchStart);
        CloseOpenHandlers(body, catchStart);
        il.Append(il.Create(OpCodes.Stloc, exception));
        il.Append(il.Create(OpCodes.Ldloc, activity));
        il.Append(il.Create(OpCodes.Brfalse, skipRecord));
        il.Append(il.Create(OpCodes.Ldloc, activity));
        il.Append(il.Create(OpCodes.Ldloc, exception));
        il.Append(il.Create(OpCodes.Call, context.RecordException));
        il.Append(skipRecord);
        il.Append(rethrow);

        il.Append(finallyStart);
        il.Append(il.Create(OpCodes.Ldloc, activity));
        il.Append(il.Create(OpCodes.Brfalse, skipDispose));
        il.Append(il.Create(OpCodes.Ldloc, activity));
        il.Append(il.Create(OpCodes.Callvirt, context.Dispose));
        il.Append(skipDispose);
        il.Append(endFinally);

        il.Append(afterHandlers);
        if (result is not null)
        {
            il.Append(il.Create(OpCodes.Ldloc, result));
        }

        il.Append(il.Create(OpCodes.Ret));

        var tryStart = prologue[0];
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = finallyStart,
            CatchType = context.ExceptionType,
        });
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = afterHandlers,
        });

        var slowStart = prologue[0];
        var fastPathInstructions = new List<Instruction>
        {
            il.Create(OpCodes.Ldsfld, context.SourceField),
            il.Create(OpCodes.Callvirt, context.HasListeners),
            il.Create(OpCodes.Brtrue, slowStart),
        };
        foreach (var instruction in originalInstructions)
        {
            fastPathInstructions.Add(fastPath[instruction]);
        }

        InsertBefore(il, slowStart, fastPathInstructions);
        AddFastPathHandlers(body, handlerSnapshots, fastPath, slowStart);

        body.MaxStackSize = Math.Max(body.MaxStackSize, 8) + 4;
        method.CustomAttributes.Add(new CustomAttribute(context.WovenConstructor));
    }

    private static void WeaveAsync(
        MethodDefinition method,
        WeaveContext context,
        string spanName,
        List<TagSite> tags,
        List<WeaveDiagnostic> diagnostics)
    {
        var attribute = GetAttribute(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        if (attribute?.ConstructorArguments.Count != 1 || attribute.ConstructorArguments[0].Value is not TypeReference stateMachineType)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "its async state machine could not be resolved")));
            return;
        }

        var stateMachine = stateMachineType.Resolve();
        var moveNext = stateMachine?.Methods.FirstOrDefault(candidate => candidate.Name == "MoveNext" && candidate.HasBody);
        var builderField = stateMachine?.Fields.FirstOrDefault(candidate => candidate.Name == "<>t__builder");
        if (stateMachine is null || moveNext is null || builderField is null || !IsSupportedBuilder(builderField.FieldType))
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "its async method builder is not supported")));
            return;
        }

        ExpandBranches(method.Body);
        ExpandBranches(moveNext.Body);

        var activityField = new FieldDefinition(ActivityFieldName, FieldAttributes.Assembly, context.ActivityType);
        stateMachine.Fields.Add(activityField);

        if (!TryPrepareAsync(method, moveNext, stateMachine, context, spanName, tags, activityField, diagnostics))
        {
            return;
        }

        if (!TryWeaveMoveNext(method, moveNext, context, activityField, builderField, diagnostics))
        {
            return;
        }

        method.CustomAttributes.Add(new CustomAttribute(context.WovenConstructor));
    }

    private static bool TryPrepareAsync(
        MethodDefinition method,
        MethodDefinition moveNext,
        TypeDefinition stateMachine,
        WeaveContext context,
        string spanName,
        List<TagSite> tags,
        FieldDefinition activityField,
        List<WeaveDiagnostic> diagnostics)
    {
        var stateField = stateMachine.Fields.FirstOrDefault(field => field.Name == "<>1__state");
        if (stateField is null)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "its async state field was not recognized")));
            return false;
        }

        var boundTags = new List<BoundTag>(tags.Count);
        foreach (var tag in tags)
        {
            var field = stateMachine.Fields.FirstOrDefault(candidate =>
                candidate.Name == tag.Parameter.Name &&
                candidate.FieldType.FullName == tag.Parameter.ParameterType.FullName);
            var storeInStub = false;
            if (field is null)
            {
                field = new FieldDefinition("<Tracepoint>tag_" + tag.Parameter.Name, FieldAttributes.Assembly, tag.Parameter.ParameterType);
                stateMachine.Fields.Add(field);
                storeInStub = true;
            }

            boundTags.Add(new BoundTag(tag, field, storeInStub));
        }

        if (boundTags.Exists(static tag => tag.StoreInStub) &&
            !TryStoreTagFields(method, boundTags, diagnostics))
        {
            return false;
        }

        var outerTry = EarliestTryStart(moveNext.Body);
        if (outerTry is null)
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "its async state machine was not recognized")));
            return false;
        }

        var il = moveNext.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldfld, stateField),
            il.Create(OpCodes.Ldc_I4_M1),
            il.Create(OpCodes.Bne_Un, skip),
            il.Create(OpCodes.Ldsfld, context.SourceField),
            il.Create(OpCodes.Callvirt, context.HasListeners),
            il.Create(OpCodes.Brfalse, skip),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldsfld, context.SourceField),
            il.Create(OpCodes.Ldstr, spanName),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Callvirt, context.StartActivity),
            il.Create(OpCodes.Stfld, activityField),
        };
        if (boundTags.Count > 0)
        {
            var skipTags = il.Create(OpCodes.Nop);
            block.Add(il.Create(OpCodes.Ldarg_0));
            block.Add(il.Create(OpCodes.Ldfld, activityField));
            block.Add(il.Create(OpCodes.Brfalse, skipTags));
            foreach (var tag in boundTags)
            {
                block.Add(il.Create(OpCodes.Ldarg_0));
                block.Add(il.Create(OpCodes.Ldfld, activityField));
                block.Add(il.Create(OpCodes.Ldstr, tag.Site.Name));
                block.Add(il.Create(OpCodes.Ldarg_0));
                block.Add(il.Create(OpCodes.Ldfld, tag.Field));
                if (tag.Site.Box)
                {
                    block.Add(il.Create(OpCodes.Box, tag.Field.FieldType));
                }

                block.Add(il.Create(OpCodes.Callvirt, context.SetTag));
                block.Add(il.Create(OpCodes.Pop));
            }

            block.Add(skipTags);
        }

        block.Add(skip);
        InsertBefore(il, outerTry, block);
        foreach (var handler in moveNext.Body.ExceptionHandlers)
        {
            if (handler.TryStart == outerTry)
            {
                handler.TryStart = block[0];
            }
        }

        moveNext.Body.MaxStackSize = Math.Max(moveNext.Body.MaxStackSize, 8) + 4;
        return true;
    }

    private static bool TryStoreTagFields(MethodDefinition method, List<BoundTag> tags, List<WeaveDiagnostic> diagnostics)
    {
        Instruction? startCall = null;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Call &&
                instruction.Operand is MethodReference called &&
                called.Name == "Start" &&
                IsSupportedBuilder(called.DeclaringType))
            {
                startCall = instruction;
                break;
            }
        }

        if (startCall?.Previous is null ||
            startCall.Previous.Operand is not VariableDefinition stateMachineVariable ||
            (startCall.Previous.OpCode != OpCodes.Ldloca && startCall.Previous.OpCode != OpCodes.Ldloca_S) ||
            !IlStack.TryFindSequenceStart(startCall, out var sequenceStart))
        {
            diagnostics.Add(Create(method, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(method), "its async state machine startup was not recognized")));
            return false;
        }

        var il = method.Body.GetILProcessor();
        var stores = new List<Instruction>();
        foreach (var tag in tags)
        {
            if (!tag.StoreInStub)
            {
                continue;
            }

            var field = method.Module.ImportReference(
                new FieldReference(tag.Field.Name, tag.Field.FieldType, stateMachineVariable.VariableType),
                method);
            EmitLoadStateMachine(il, stores, stateMachineVariable);
            stores.Add(il.Create(OpCodes.Ldarg, tag.Site.Parameter));
            stores.Add(il.Create(OpCodes.Stfld, field));
        }

        InsertBefore(il, sequenceStart, stores);
        return true;
    }

    private static Instruction? EarliestTryStart(MethodBody body)
    {
        Instruction? earliest = null;
        var earliestIndex = int.MaxValue;
        foreach (var handler in body.ExceptionHandlers)
        {
            if (handler.HandlerType != ExceptionHandlerType.Catch || handler.TryStart is null)
            {
                continue;
            }

            var index = body.Instructions.IndexOf(handler.TryStart);
            if (index >= 0 && index < earliestIndex)
            {
                earliestIndex = index;
                earliest = handler.TryStart;
            }
        }

        return earliest;
    }


    private static bool TryWeaveMoveNext(
        MethodDefinition source,
        MethodDefinition moveNext,
        WeaveContext context,
        FieldDefinition activityField,
        FieldDefinition builderField,
        List<WeaveDiagnostic> diagnostics)
    {
        var completions = new List<(Instruction Call, VariableDefinition? Exception)>();
        foreach (var instruction in moveNext.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference called ||
                (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt) ||
                called.Name is not ("SetResult" or "SetException") ||
                !SameType(called.DeclaringType, builderField.FieldType))
            {
                continue;
            }

            if (called.Name == "SetException")
            {
                var previous = instruction.Previous;
                if (previous is null || !IlStack.TryGetLoadedVariable(moveNext.Body, previous, out var exception))
                {
                    diagnostics.Add(Create(source, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(source), "its async exception path was not recognized")));
                    return false;
                }

                completions.Add((instruction, exception));
            }
            else
            {
                completions.Add((instruction, null));
            }
        }

        if (completions.Count == 0)
        {
            diagnostics.Add(Create(source, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(source), "its async completion path was not recognized")));
            return false;
        }

        var il = moveNext.Body.GetILProcessor();
        foreach (var (call, exception) in completions)
        {
            if (!IlStack.TryFindSequenceStart(call, out var sequenceStart))
            {
                diagnostics.Add(Create(source, DiagnosticMessages.UnsupportedMethodId, DiagnosticMessages.UnsupportedMethod(Display(source), "its async completion path was not recognized")));
                return false;
            }

            InsertBefore(il, sequenceStart, EmitStop(il, activityField, context, exception));
        }

        moveNext.Body.MaxStackSize = Math.Max(moveNext.Body.MaxStackSize, 8) + 4;
        return true;
    }

    private static List<Instruction> EmitStop(
        ILProcessor il,
        FieldDefinition activityField,
        WeaveContext context,
        VariableDefinition? exception)
    {
        var skip = il.Create(OpCodes.Nop);
        var instructions = new List<Instruction>
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldfld, activityField),
            il.Create(OpCodes.Brfalse, skip),
        };

        if (exception is not null)
        {
            instructions.Add(il.Create(OpCodes.Ldarg_0));
            instructions.Add(il.Create(OpCodes.Ldfld, activityField));
            instructions.Add(il.Create(OpCodes.Ldloc, exception));
            instructions.Add(il.Create(OpCodes.Call, context.RecordException));
        }

        instructions.Add(il.Create(OpCodes.Ldarg_0));
        instructions.Add(il.Create(OpCodes.Ldfld, activityField));
        instructions.Add(il.Create(OpCodes.Callvirt, context.Dispose));
        instructions.Add(skip);
        return instructions;
    }

    private static void AppendTags(
        ILProcessor il,
        List<Instruction> instructions,
        List<TagSite> tags,
        WeaveContext context,
        Action<List<Instruction>> emitLoadActivity)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var skip = il.Create(OpCodes.Nop);
        emitLoadActivity(instructions);
        instructions.Add(il.Create(OpCodes.Brfalse, skip));
        foreach (var tag in tags)
        {
            emitLoadActivity(instructions);
            instructions.Add(il.Create(OpCodes.Ldstr, tag.Name));
            instructions.Add(il.Create(OpCodes.Ldarg, tag.Parameter));
            if (tag.Box)
            {
                instructions.Add(il.Create(OpCodes.Box, tag.Parameter.ParameterType));
            }

            instructions.Add(il.Create(OpCodes.Callvirt, context.SetTag));
            instructions.Add(il.Create(OpCodes.Pop));
        }

        instructions.Add(skip);
    }

    private static void EmitLoadStateMachine(ILProcessor il, List<Instruction> instructions, VariableDefinition stateMachine)
    {
        instructions.Add(IsValueType(stateMachine.VariableType)
            ? il.Create(OpCodes.Ldloca, stateMachine)
            : il.Create(OpCodes.Ldloc, stateMachine));
    }

    private static bool IsValueType(TypeReference type)
    {
        if (type.IsValueType)
        {
            return true;
        }

        try
        {
            return type.Resolve()?.IsValueType == true;
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    private static void CloseOpenHandlers(MethodBody body, Instruction boundary)
    {
        foreach (var handler in body.ExceptionHandlers)
        {
            handler.TryEnd ??= boundary;
            handler.HandlerEnd ??= boundary;
        }
    }

    private static void ExpandBranches(MethodBody body)
    {
        foreach (var instruction in body.Instructions)
        {
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br,
                Code.Brfalse_S => OpCodes.Brfalse,
                Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq,
                Code.Bge_S => OpCodes.Bge,
                Code.Bgt_S => OpCodes.Bgt,
                Code.Ble_S => OpCodes.Ble,
                Code.Blt_S => OpCodes.Blt,
                Code.Bne_Un_S => OpCodes.Bne_Un,
                Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_Un_S => OpCodes.Ble_Un,
                Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Leave_S => OpCodes.Leave,
                _ => instruction.OpCode,
            };
        }
    }

    private static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case Instruction target when target == from:
                    instruction.Operand = to;
                    break;
                case Instruction[] targets:
                    for (var i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] == from)
                        {
                            targets[i] = to;
                        }
                    }

                    break;
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            if (handler.TryStart == from)
            {
                handler.TryStart = to;
            }

            if (handler.TryEnd == from)
            {
                handler.TryEnd = to;
            }

            if (handler.HandlerStart == from)
            {
                handler.HandlerStart = to;
            }

            if (handler.HandlerEnd == from)
            {
                handler.HandlerEnd = to;
            }

            if (handler.FilterStart == from)
            {
                handler.FilterStart = to;
            }
        }
    }

    private static void InsertBefore(ILProcessor il, Instruction anchor, List<Instruction> instructions)
    {
        Instruction? previous = null;
        foreach (var instruction in instructions)
        {
            if (previous is null)
            {
                il.InsertBefore(anchor, instruction);
            }
            else
            {
                il.InsertAfter(previous, instruction);
            }

            previous = instruction;
        }
    }

    private static bool TryGetSpanName(MethodDefinition method, out string spanName, out WeaveDiagnostic diagnostic)
    {
        var attribute = GetAttribute(method, "Tracepoint.TraceAttribute");
        if (attribute is not null &&
            attribute.ConstructorArguments.Count == 1 &&
            attribute.ConstructorArguments[0].Value is string explicitName)
        {
            if (string.IsNullOrWhiteSpace(explicitName))
            {
                spanName = "";
                diagnostic = Create(method, DiagnosticMessages.InvalidNameId, DiagnosticMessages.InvalidName("Trace name", explicitName));
                return false;
            }

            spanName = explicitName;
            diagnostic = null!;
            return true;
        }

        spanName = Display(method);
        diagnostic = null!;
        return true;
    }

    private static bool TryGetTags(MethodDefinition method, List<WeaveDiagnostic> diagnostics, out List<TagSite> tags)
    {
        tags = [];
        var ok = true;
        foreach (var parameter in method.Parameters)
        {
            var attribute = GetAttribute(parameter, "Tracepoint.TraceTagAttribute");
            if (attribute is null)
            {
                continue;
            }

            string? explicitName = null;
            if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string value)
            {
                explicitName = value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    diagnostics.Add(Create(method, DiagnosticMessages.InvalidNameId, DiagnosticMessages.InvalidName("Trace tag name", value)));
                    ok = false;
                    continue;
                }
            }

            var name = explicitName ?? parameter.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(Create(method, DiagnosticMessages.InvalidNameId, DiagnosticMessages.InvalidName("Trace tag name", name ?? "")));
                ok = false;
                continue;
            }

            if (parameter.ParameterType.IsByReference || !TryGetTagKind(parameter.ParameterType, out var box))
            {
                diagnostics.Add(Create(
                    method,
                    DiagnosticMessages.UnsupportedTagId,
                    DiagnosticMessages.UnsupportedTag(name, parameter.ParameterType.FullName)));
                ok = false;
                continue;
            }

            tags.Add(new TagSite(name, parameter, box));
        }

        return ok;
    }

    private static bool TryGetTagKind(TypeReference type, out bool box)
    {
        switch (type.FullName)
        {
            case "System.String":
                box = false;
                return true;
            case "System.Boolean":
            case "System.Int32":
            case "System.Int64":
            case "System.Double":
                box = true;
                return true;
            default:
                box = false;
                return false;
        }
    }

    private static bool IsSupportedBuilder(TypeReference type)
    {
        var name = ElementName(type);
        return name is "System.Runtime.CompilerServices.AsyncTaskMethodBuilder"
            or "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"
            or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"
            or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1";
    }

    private static bool SameType(TypeReference left, TypeReference right) =>
        ElementName(left) == ElementName(right);

    private static string ElementName(TypeReference type) =>
        type is GenericInstanceType generic ? generic.ElementType.FullName : type.FullName;

    private static CustomAttribute? GetAttribute(ICustomAttributeProvider provider, string fullName)
    {
        if (!provider.HasCustomAttributes)
        {
            return null;
        }

        foreach (var attribute in provider.CustomAttributes)
        {
            if (attribute.AttributeType.FullName == fullName)
            {
                return attribute;
            }
        }

        return null;
    }

    private static string Display(MethodDefinition method) =>
        DisplayType(method.DeclaringType) + "." + method.Name;

    private static string DisplayType(TypeReference type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return type.DeclaringType is null ? name : DisplayType(type.DeclaringType) + "." + name;
    }

    private static WeaveDiagnostic Create(MethodDefinition method, string id, string message)
    {
        var point = method.DebugInformation?.SequencePoints.FirstOrDefault(candidate => candidate is not null && !candidate.IsHidden);
        return new WeaveDiagnostic(id, message, point?.Document?.Url, point?.StartLine ?? 0, point?.StartColumn ?? 0);
    }

    private static List<HandlerSnapshot> SnapshotHandlers(MethodBody body)
    {
        var snapshots = new List<HandlerSnapshot>(body.ExceptionHandlers.Count);
        foreach (var handler in body.ExceptionHandlers)
        {
            snapshots.Add(new HandlerSnapshot(
                handler.HandlerType,
                handler.CatchType,
                handler.TryStart,
                handler.TryEnd,
                handler.HandlerStart,
                handler.HandlerEnd,
                handler.FilterStart));
        }

        return snapshots;
    }

    private static Dictionary<Instruction, Instruction> CloneInstructions(IReadOnlyList<Instruction> source)
    {
        var map = new Dictionary<Instruction, Instruction>(source.Count);
        foreach (var instruction in source)
        {
            var clone = Instruction.Create(OpCodes.Nop);
            clone.OpCode = instruction.OpCode;
            clone.Operand = instruction.Operand;
            map.Add(instruction, clone);
        }

        foreach (var instruction in source)
        {
            var clone = map[instruction];
            switch (clone.Operand)
            {
                case Instruction target:
                    clone.Operand = map[target];
                    break;
                case Instruction[] targets:
                    var remapped = new Instruction[targets.Length];
                    for (var i = 0; i < targets.Length; i++)
                    {
                        remapped[i] = map[targets[i]];
                    }

                    clone.Operand = remapped;
                    break;
            }
        }

        return map;
    }

    private static void AddFastPathHandlers(
        MethodBody body,
        List<HandlerSnapshot> snapshots,
        Dictionary<Instruction, Instruction> map,
        Instruction slowStart)
    {
        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            var snapshot = snapshots[i];
            body.ExceptionHandlers.Insert(0, new ExceptionHandler(snapshot.HandlerType)
            {
                CatchType = snapshot.CatchType,
                TryStart = map[snapshot.TryStart],
                TryEnd = snapshot.TryEnd is null ? slowStart : map[snapshot.TryEnd],
                HandlerStart = map[snapshot.HandlerStart],
                HandlerEnd = snapshot.HandlerEnd is null ? slowStart : map[snapshot.HandlerEnd],
                FilterStart = snapshot.FilterStart is null ? null : map[snapshot.FilterStart],
            });
        }
    }

    private readonly struct HandlerSnapshot
    {
        internal HandlerSnapshot(
            ExceptionHandlerType handlerType,
            TypeReference? catchType,
            Instruction tryStart,
            Instruction? tryEnd,
            Instruction handlerStart,
            Instruction? handlerEnd,
            Instruction? filterStart)
        {
            HandlerType = handlerType;
            CatchType = catchType;
            TryStart = tryStart;
            TryEnd = tryEnd;
            HandlerStart = handlerStart;
            HandlerEnd = handlerEnd;
            FilterStart = filterStart;
        }

        internal ExceptionHandlerType HandlerType { get; }

        internal TypeReference? CatchType { get; }

        internal Instruction TryStart { get; }

        internal Instruction? TryEnd { get; }

        internal Instruction HandlerStart { get; }

        internal Instruction? HandlerEnd { get; }

        internal Instruction? FilterStart { get; }
    }
}

internal readonly struct TagSite
{
    internal TagSite(string name, ParameterDefinition parameter, bool box)
    {
        Name = name;
        Parameter = parameter;
        Box = box;
    }

    internal string Name { get; }

    internal ParameterDefinition Parameter { get; }

    internal bool Box { get; }
}

internal readonly struct BoundTag
{
    internal BoundTag(TagSite site, FieldDefinition field, bool storeInStub)
    {
        Site = site;
        Field = field;
        StoreInStub = storeInStub;
    }

    internal TagSite Site { get; }

    internal FieldDefinition Field { get; }

    internal bool StoreInStub { get; }
}
