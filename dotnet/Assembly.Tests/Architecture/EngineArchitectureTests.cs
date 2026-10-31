using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Assembly.Application.Features.Engine;

namespace Assembly.Tests.Architecture;

public sealed class EngineArchitectureTests
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => op.Value);

    [Fact]
    public void Engine_types_reference_no_outside_systems()
    {
        var assembly = typeof(EngineJson).Assembly;
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var engineTypes = assembly.GetTypes().Where(t => t.Namespace == "Assembly.Application.Features.Engine"
            || t.Namespace?.StartsWith("Assembly.Application.Features.Engine.", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(engineTypes);
        foreach (var type in engineTypes)
        {
            CheckType(type.BaseType);
            foreach (var contract in type.GetInterfaces())
                CheckType(contract);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
                CheckType(field.FieldType);
            foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
            {
                Assert.False((method.Attributes & MethodAttributes.PinvokeImpl) != 0);
                if (method is MethodInfo info)
                    CheckType(info.ReturnType);
                foreach (var parameter in method.GetParameters())
                    CheckType(parameter.ParameterType);
                var definition = metadata.GetMethodDefinition(
                    System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0xffffff));
                if (definition.RelativeVirtualAddress == 0)
                    continue;
                var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
                foreach (var local in method.GetMethodBody()!.LocalVariables)
                    CheckType(local.LocalType);
                foreach (var clause in method.GetMethodBody()!.ExceptionHandlingClauses)
                    if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
                        CheckType(clause.CatchType);
                CheckInstructions(body.GetILReader(), method);
            }
        }
    }

    private static void CheckInstructions(BlobReader il, MethodBase method)
    {
        while (il.RemainingBytes > 0)
        {
            var first = il.ReadByte();
            var code = first == 0xfe ? unchecked((short)(0xfe00 | il.ReadByte())) : (short)first;
            var operand = OpCodesByValue[code].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(il.ReadInt32(), method.DeclaringType!.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                CheckType(member as Type ?? member.DeclaringType);
                if (member is FieldInfo field)
                    CheckType(field.FieldType);
                if (member is MethodBase called)
                {
                    if (called is MethodInfo info)
                        CheckType(info.ReturnType);
                    foreach (var parameter in called.GetParameters())
                        CheckType(parameter.ParameterType);
                    Assert.False(called is ConstructorInfo
                        && called.DeclaringType?.FullName is "System.IO.StreamReader" or "System.IO.StreamWriter"
                        && called.GetParameters().FirstOrDefault()?.ParameterType == typeof(string));
                }
                continue;
            }
            var size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => il.ReadInt32() * 4,
                _ => 4
            };
            il.Offset += size;
        }
    }

    private static void CheckType(Type? type)
    {
        if (type is null)
            return;
        var name = type.FullName ?? type.Name;
        string[] files = ["System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.IO.FileInfo", "System.IO.DirectoryInfo"];
        Assert.DoesNotContain(files, file => name == file || name.StartsWith(file + "+", StringComparison.Ordinal));
        Assert.False(name.StartsWith("Assembly.Infrastructure", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal), name);
        if (type.HasElementType)
            CheckType(type.GetElementType());
        foreach (var argument in type.GetGenericArguments())
            CheckType(argument);
    }
}
