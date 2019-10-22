﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UpdateFieldCodeGenerator.Formats
{
    public class TrinityCoreHandler : UpdateFieldHandlerBase
    {
        private readonly Type _updateFieldType = CppTypes.CreateType("UpdateField", "T", "BlockBit", "Bit");
        private readonly Type _arrayUpdateFieldType = CppTypes.CreateType("UpdateFieldArray", "T", "Size", "PrimaryBit", "FirstElementBit");
        private readonly Type _dynamicUpdateFieldType = CppTypes.CreateType("DynamicUpdateField", "T", "BlockBit", "Bit");

        private UpdateFieldFlag _allUsedFlags;
        private readonly IDictionary<int, UpdateFieldFlag> _flagByUpdateBit = new Dictionary<int, UpdateFieldFlag>();

        private readonly string _changesMaskName = "_changesMask";
        private string _owningObjectType;
        private readonly IList<Action> _delayedHeaderWrites = new List<Action>();
        private readonly IList<string> _changesMaskClears = new List<string>();

        public TrinityCoreHandler() : base(new StreamWriter("UpdateFields.cpp"), new StreamWriter("UpdateFields.h"))
        {
        }

        public override void BeforeStructures()
        {
            var forwardDeclareTypes = Enum.GetValues(typeof(ObjectType))
                .Cast<ObjectType>()
                .Select(objectType => GetClassForObjectType(objectType))
                .Distinct()
                .Concat(Enumerable.Repeat("ByteBuffer", 1))
                .OrderBy(o => o);

            WriteLicense(_header);
            _header.WriteLine("#ifndef UpdateFields_h__");
            _header.WriteLine("#define UpdateFields_h__");
            _header.WriteLine();
            _header.WriteLine("#include \"EnumClassFlag.h\"");
            _header.WriteLine("#include \"ObjectGuid.h\"");
            _header.WriteLine("#include \"Position.h\"");
            _header.WriteLine("#include \"QuaternionData.h\"");
            _header.WriteLine("#include \"UpdateField.h\"");
            _header.WriteLine("#include \"UpdateMask.h\"");
            _header.WriteLine();
            foreach (var fwd in forwardDeclareTypes)
                _header.WriteLine($"class {fwd};");
            _header.WriteLine();
            _header.WriteLine("namespace UF");
            _header.WriteLine("{");

            WriteLicense(_source);
            _source.WriteLine("#include \"UpdateFields.h\"");
            _source.WriteLine("#include \"ByteBuffer.h\"");
            _source.WriteLine("#include \"Player.h\"");
            _source.WriteLine("#include \"ViewerDependentValues.h\"");
            _source.WriteLine();
            _source.WriteLine("#if TRINITY_COMPILER == TRINITY_COMPILER_GNU");
            _source.WriteLine("#pragma GCC diagnostic push");
            _source.WriteLine("#pragma GCC diagnostic ignored \"-Wunused-parameter\"");
            _source.WriteLine("#else");
            _source.WriteLine("#pragma warning(push)");
            _source.WriteLine("#pragma warning(disable: 4100)");
            _source.WriteLine("#endif");
            _source.WriteLine();
            _source.WriteLine("namespace UF");
            _source.WriteLine("{");
        }

        public override void AfterStructures()
        {
            _header.WriteLine("}");
            _header.WriteLine();
            _header.WriteLine("#endif // UpdateFields_h__");

            _source.WriteLine("}");
            _source.WriteLine();
            _source.WriteLine("#if TRINITY_COMPILER == TRINITY_COMPILER_GNU");
            _source.WriteLine("#pragma GCC diagnostic pop");
            _source.WriteLine("#else");
            _source.WriteLine("#pragma warning(pop)");
            _source.WriteLine("#endif");
        }

        public override void OnStructureBegin(Type structureType, ObjectType objectType, bool create, bool writeUpdateMasks)
        {
            base.OnStructureBegin(structureType, objectType, create, writeUpdateMasks);
            _allUsedFlags = UpdateFieldFlag.None;
            _flagByUpdateBit.Clear();
            _flagByUpdateBit[0] = UpdateFieldFlag.None;
            _owningObjectType = GetClassForObjectType(objectType);
            _delayedHeaderWrites.Clear();
            _changesMaskClears.Clear();

            var structureName = RenameType(structureType);

            if (_create)
                _source.WriteLine($"void {structureName}::WriteCreate(ByteBuffer& data, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const");
            else
                _source.WriteLine($"void {structureName}::WriteUpdate(ByteBuffer& data, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const");

            _source.WriteLine("{");
        }

        public override void OnStructureEnd(bool needsFlush, bool forceMaskMask)
        {
            ++_bitCounter;
            if (!_create)
            {
                _header.Write($"struct {RenameType(_structureType)} : public IsUpdateFieldStructureTag");
                if (_writeUpdateMasks)
                    _header.Write($", public HasChangesMask<{_bitCounter}>");
                _header.WriteLine();
                _header.WriteLine("{");

                foreach (var headerWrite in _delayedHeaderWrites)
                    headerWrite();

                _header.WriteLine();
                _header.WriteLine($"    void WriteCreate(ByteBuffer& data, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const;");
                _header.WriteLine($"    void WriteUpdate(ByteBuffer& data, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const;");
                if (_writeUpdateMasks)
                {
                    if (_allUsedFlags != UpdateFieldFlag.None)
                    {
                        _header.WriteLine($"    void AppendAllowedFieldsMaskForFlag(UpdateMask<{_bitCounter}>& allowedMaskForTarget, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags) const;");
                        _header.WriteLine($"    void WriteUpdate(ByteBuffer& data, UpdateMask<{_bitCounter}> const& changesMask, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const;");
                    }
                    _header.WriteLine("    void ClearChangesMask();");
                }
                foreach (var dynamicChangesMaskType in _dynamicChangesMaskTypes)
                    _header.WriteLine($"    bool Is{RenameType(dynamicChangesMaskType)}DynamicChangesMask() const {{ return false; }} // bandwidth savings aren't worth the cpu time");

                _header.WriteLine("};");
                _header.WriteLine();
                _header.Flush();
            }

            if (!_create && _writeUpdateMasks)
            {
                var bitMaskByFlag = new Dictionary<UpdateFieldFlag, BitArray>();
                if (_allUsedFlags != UpdateFieldFlag.None)
                {
                    for (var i = 0; i < _bitCounter; ++i)
                    {
                        if (_flagByUpdateBit.TryGetValue(i, out var flag) && flag != UpdateFieldFlag.None)
                        {
                            for (var j = 0; j < 8; ++j)
                                if ((flag & (UpdateFieldFlag)(1 << j)) != UpdateFieldFlag.None)
                                    bitMaskByFlag.ComputeIfAbsent((UpdateFieldFlag)(1 << j), k => new BitArray(_bitCounter)).Set(i, true);
                        }
                        else
                            bitMaskByFlag.ComputeIfAbsent(UpdateFieldFlag.None, k => new BitArray(_bitCounter)).Set(i, true);
                    }

                    var noneFlags = new int[(_bitCounter + 31) / 32];
                    bitMaskByFlag[UpdateFieldFlag.None].CopyTo(noneFlags, 0);

                    _source.WriteLine($"    UpdateMask<{_bitCounter}> allowedMaskForTarget({{ {string.Join(", ", noneFlags.Select(v => "0x" + v.ToString("X8") + "u"))} }});");
                    _source.WriteLine($"    AppendAllowedFieldsMaskForFlag(allowedMaskForTarget, fieldVisibilityFlags);");
                    if (_allUsedFlags != UpdateFieldFlag.None)
                        _source.WriteLine($"    WriteUpdate(data, {_changesMaskName} & allowedMaskForTarget, fieldVisibilityFlags, owner, receiver);");
                    else
                        _source.WriteLine($"    UpdateMask<{_bitCounter}> changesMask = {_changesMaskName} & allowedMaskForTarget;");
                }
                else
                {
                    if (_allUsedFlags != UpdateFieldFlag.None)
                        _source.WriteLine($"    WriteUpdate(data, {_changesMaskName}, fieldVisibilityFlags, owner, receiver);");
                    else
                        _source.WriteLine($"    UpdateMask<{_bitCounter}> const& changesMask = {_changesMaskName};");
                }

                if (_allUsedFlags != UpdateFieldFlag.None)
                {
                    _source.WriteLine("}");
                    _source.WriteLine();
                    _source.WriteLine($"void {RenameType(_structureType)}::AppendAllowedFieldsMaskForFlag(UpdateMask<{_bitCounter}>& allowedMaskForTarget, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags) const");
                    _source.WriteLine("{");
                    for (var j = 0; j < 8; ++j)
                    {
                        if ((_allUsedFlags & (UpdateFieldFlag)(1 << j)) != UpdateFieldFlag.None)
                        {
                            var flagArray = new int[(_bitCounter + 31) / 32];
                            bitMaskByFlag[(UpdateFieldFlag)(1 << j)].CopyTo(flagArray, 0);
                            _source.WriteLine($"    if (fieldVisibilityFlags.HasFlag(UpdateFieldFlag::{(UpdateFieldFlag)(1 << j)}))");
                            _source.WriteLine($"        allowedMaskForTarget |= {{ {string.Join(", ", flagArray.Select(v => "0x" + v.ToString("X8") + "u"))} }};");
                        }
                    }
                    _source.WriteLine("}");
                    _source.WriteLine();
                    _source.WriteLine($"void {RenameType(_structureType)}::WriteUpdate(ByteBuffer& data, UpdateMask<{_bitCounter}> const& changesMask, EnumClassFlag<UpdateFieldFlag> fieldVisibilityFlags, {_owningObjectType} const* owner, Player const* receiver) const");
                    _source.WriteLine("{");
                }

                var maskBlocks = (_bitCounter + 31) / 32;
                if (maskBlocks > 1 || forceMaskMask)
                {
                    if (maskBlocks >= 32)
                    {
                        _source.WriteLine($"    for (std::size_t i = 0; i < {maskBlocks / 32}; ++i)");
                        _source.WriteLine($"        data << uint32(changesMask.GetBlocksMask(i));");
                        if ((maskBlocks % 32) != 0)
                            _source.WriteLine($"    data.WriteBits(changesMask.GetBlocksMask({maskBlocks / 32}), {maskBlocks % 32});");
                    }
                    else
                        _source.WriteLine($"    data.WriteBits(changesMask.GetBlocksMask(0), {maskBlocks});");

                    if (maskBlocks > 1)
                    {
                        _source.WriteLine($"    for (std::size_t i = 0; i < {maskBlocks}; ++i)");
                        _source.WriteLine("        if (changesMask.GetBlock(i))");
                        _source.WriteLine("            data.WriteBits(changesMask.GetBlock(i), 32);");
                    }
                    else
                    {
                        _source.WriteLine("    if (changesMask.GetBlock(0))");
                        _source.WriteLine("        data.WriteBits(changesMask.GetBlock(0), 32);");
                    }
                }
                else
                    _source.WriteLine($"    data.WriteBits(changesMask.GetBlock(0), {_bitCounter});");

                _source.WriteLine();
            }

            PostProcessFieldWrites();

            if (!_create)
            {
                foreach (var dynamicChangesMaskType in _dynamicChangesMaskTypes)
                {
                    var typeName = RenameType(dynamicChangesMaskType);
                    _source.WriteLine($"    bool has{typeName}DynamicChangesMask = data.WriteBit(Is{typeName}DynamicChangesMask());");
                }
            }

            List<FlowControlBlock> previousFlowControl = null;
            foreach (var (_, _, Write) in _fieldWrites)
                previousFlowControl = Write(previousFlowControl);

            if (needsFlush)
                _source.WriteLine($"{GetIndent()}data.FlushBits();");

            _source.WriteLine("}");
            _source.WriteLine();

            if (_writeUpdateMasks)
            {
                _source.WriteLine($"void {RenameType(_structureType)}::ClearChangesMask()");
                _source.WriteLine("{");
                foreach (var clear in _changesMaskClears)
                    _source.WriteLine(clear);
                _source.WriteLine($"    {_changesMaskName}.ResetAll();");
                _source.WriteLine("}");
                _source.WriteLine();
            }

            _source.Flush();
        }

        public override IReadOnlyList<FlowControlBlock> OnField(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            _allUsedFlags |= updateField.Flag;

            name = RenameField(name);

            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ({updateField.Flag.ToFlagsExpression(" || ", "fieldVisibilityFlags.HasFlag(UpdateFieldFlag::", ")")})" });

            var type = updateField.Type;
            var nameUsedToWrite = name;
            var access = "->";
            var arrayLoopBlockIndex = -1;
            var indexLetter = 'i';
            var allIndexes = "";
            if (type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (std::size_t {indexLetter} = 0; {indexLetter} < {updateField.Size}; ++{indexLetter})" });
                nameUsedToWrite += $"[{indexLetter}]";
                access = ".";
                type = type.GetElementType();
                arrayLoopBlockIndex = flowControl.Count;
                allIndexes += ", " + indexLetter;
                ++indexLetter;
            }
            if (typeof(DynamicUpdateField).IsAssignableFrom(type))
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (std::size_t {indexLetter} = 0; {indexLetter} < {nameUsedToWrite}.size(); ++{indexLetter})" });
                if (!_create)
                    flowControl.Add(new FlowControlBlock { Statement = $"if ({nameUsedToWrite}.HasChanged({indexLetter}))" });

                nameUsedToWrite += $"[{indexLetter}]";
                access = ".";
                type = type.GenericTypeArguments[0];
                allIndexes += ", " + indexLetter;
                ++indexLetter;
            }
            if (typeof(BlzVectorField).IsAssignableFrom(type))
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (std::size_t {indexLetter} = 0; {indexLetter} < {name}->size(); ++{indexLetter})" });
                nameUsedToWrite = $"(*{nameUsedToWrite})[{indexLetter}]";
                access = ".";
                type = type.GenericTypeArguments[0];
                allIndexes += ", " + indexLetter;
                ++indexLetter;
            }
            if ((updateField.CustomFlag & CustomUpdateFieldFlag.ViewerDependent) != CustomUpdateFieldFlag.None)
                nameUsedToWrite = $"ViewerDependentValue<{name}Tag>::GetValue({nameUsedToWrite}{allIndexes}, owner, receiver)";

            if (!_create && _writeUpdateMasks)
                GenerateBitIndexConditions(updateField, name, flowControl, previousControlFlow, arrayLoopBlockIndex);

            RegisterDynamicChangesMaskFieldType(type);

            _fieldWrites.Add((name, false, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                WriteField(nameUsedToWrite, access, type, updateField.BitSize);
                _indent = 1;
                return flowControl;
            }
            ));

            if (!_create && updateField.SizeForField == null)
            {
                _delayedHeaderWrites.Add(() =>
                {
                    WriteFieldDeclaration(name, updateField);
                });
                if (_writeUpdateMasks)
                    _changesMaskClears.Add($"    Base::ClearChangesMask({name});");
            }

            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnDynamicFieldSizeCreate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);

            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ({updateField.Flag.ToFlagsExpression(" || ", "fieldVisibilityFlags.HasFlag(UpdateFieldFlag::", ")")})" });

            var nameUsedToWrite = name;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (std::size_t i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
            }

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                _source.WriteLine($"{GetIndent()}data << uint32({nameUsedToWrite}.size());");
                _indent = 1;
                return flowControl;
            }));
            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnDynamicFieldSizeUpdate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);

            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ({updateField.Flag.ToFlagsExpression(" || ", "fieldVisibilityFlags.HasFlag(UpdateFieldFlag::", ")")})" });

            var nameUsedToWrite = name;
            var arrayLoopBlockIndex = -1;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (std::size_t i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
                arrayLoopBlockIndex = flowControl.Count;
            }

            if (_writeUpdateMasks)
                GenerateBitIndexConditions(updateField, name, flowControl, previousControlFlow, arrayLoopBlockIndex);

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                _source.WriteLine($"{GetIndent()}{nameUsedToWrite}.WriteUpdateMask(data);");
                _indent = 1;
                return flowControl;
            }));
            return flowControl;
        }

        private void GenerateBitIndexConditions(UpdateField updateField, string name, List<FlowControlBlock> flowControl, IReadOnlyList<FlowControlBlock> previousControlFlow, int arrayLoopBlockIndex)
        {
            var newField = false;
            var nameForIndex = updateField.SizeForField != null ? RenameField(updateField.SizeForField.Name) : name;
            if (!_fieldBitIndex.TryGetValue(nameForIndex, out var bitIndex))
            {
                bitIndex = new List<int>();
                if (flowControl.Count == 0 || !FlowControlBlock.AreChainsAlmostEqual(previousControlFlow, flowControl))
                {
                    if (!updateField.Type.IsArray)
                    {
                        ++_nonArrayBitCounter;
                        if (_nonArrayBitCounter == 32)
                        {
                            _blockGroupBit = ++_bitCounter;
                            _nonArrayBitCounter = 1;
                        }
                    }

                    bitIndex.Add(++_bitCounter);

                    if (!updateField.Type.IsArray)
                        bitIndex.Add(_blockGroupBit);
                }
                else
                {
                    if (_previousFieldCounters == null || _previousFieldCounters.Count == 1)
                        throw new Exception("Expected previous field to have been an array");

                    bitIndex.Add(_previousFieldCounters[0]);
                }

                _fieldBitIndex[nameForIndex] = bitIndex;
                newField = true;
            }

            if (_flagByUpdateBit.ContainsKey(bitIndex[0]))
                _flagByUpdateBit[bitIndex[0]] |= updateField.Flag;
            else
                _flagByUpdateBit[bitIndex[0]] = updateField.Flag;

            if (updateField.Type.IsArray)
            {
                flowControl.Insert(0, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[0]}])" });
                if (newField)
                {
                    bitIndex.AddRange(Enumerable.Range(_bitCounter + 1, updateField.Size));
                    _bitCounter += updateField.Size;
                }
                flowControl.Insert(arrayLoopBlockIndex + 1, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[1]} + i])" });
                for (var i = 0; i < updateField.Size; ++i)
                    _flagByUpdateBit[bitIndex[1] + i] = updateField.Flag;
            }
            else
            {
                flowControl.Insert(0, new FlowControlBlock { Statement = $"if (changesMask[{_blockGroupBit}])" });
                flowControl.Insert(1, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[0]}])" });
            }

            _previousFieldCounters = bitIndex;
        }

        private void WriteField(string name, string access, Type type, int bitSize)
        {
            _source.Write(GetIndent());
            if (name.EndsWith("size()"))
            {
                if (_create)
                    _source.WriteLine($"data << uint32({name});");
                else
                    _source.WriteLine($"data.WriteBits({name}, 32);");
                return;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Object:
                    if (type == typeof(WowGuid))
                        _source.WriteLine($"data << {name};");
                    else if (type == typeof(Bits))
                        _source.WriteLine($"data.WriteBits({name}, {bitSize});");
                    else if (type == typeof(Vector2))
                        _source.WriteLine($"data << {name};");
                    else if (type == typeof(Quaternion))
                    {
                        _source.WriteLine($"data << float({name}{access}x);");
                        _source.WriteLine($"{GetIndent()}data << float({name}{access}y);");
                        _source.WriteLine($"{GetIndent()}data << float({name}{access}z);");
                        _source.WriteLine($"{GetIndent()}data << float({name}{access}w);");
                    }
                    else if (_create)
                        _source.WriteLine($"{name}{access}WriteCreate(data, fieldVisibilityFlags, owner, receiver);");
                    else
                    {
                        if (_dynamicChangesMaskTypes.Contains(type.Name))
                        {
                            _source.WriteLine($"if (has{RenameType(type.Name)}DynamicChangesMask)");
                            _source.WriteLine($"{GetIndent()}    {name}{access}WriteUpdate(data, fieldVisibilityFlags, owner, receiver);");
                            _source.WriteLine($"{GetIndent()}else");
                            _source.WriteLine($"{GetIndent()}    {name}{access}WriteCreate(data, fieldVisibilityFlags, owner, receiver);");

                        }
                        else
                            _source.WriteLine($"{name}{access}WriteUpdate(data, fieldVisibilityFlags, owner, receiver);");
                    }
                    break;
                case TypeCode.Boolean:
                    _source.WriteLine($"data.WriteBit({name});");
                    break;
                case TypeCode.SByte:
                    _source.WriteLine($"data << int8({name});");
                    break;
                case TypeCode.Byte:
                    _source.WriteLine($"data << uint8({name});");
                    break;
                case TypeCode.Int16:
                    _source.WriteLine($"data << int16({name});");
                    break;
                case TypeCode.UInt16:
                    _source.WriteLine($"data << uint16({name});");
                    break;
                case TypeCode.Int32:
                    _source.WriteLine($"data << int32({name});");
                    break;
                case TypeCode.UInt32:
                    _source.WriteLine($"data << uint32({name});");
                    break;
                case TypeCode.Int64:
                    _source.WriteLine($"data << int64({name});");
                    break;
                case TypeCode.UInt64:
                    _source.WriteLine($"data << uint64({name});");
                    break;
                case TypeCode.Single:
                    _source.WriteLine($"data << float({name});");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private Type PrepareFieldType(Type originalType)
        {
            var cppType = CppTypes.GetCppType(originalType);
            if (originalType.IsGenericType)
                return cppType.GetGenericTypeDefinition().MakeGenericType(cppType.GenericTypeArguments.Select(gp => PrepareFieldType(gp)).ToArray());

            if (cppType.Assembly == Assembly.GetExecutingAssembly() && !cppType.Assembly.IsDynamic)
                return CppTypes.CreateType(RenameType(cppType));

            return cppType;
        }

        private void WriteFieldDeclaration(string name, UpdateField declarationType)
        {
            var fieldGeneratedType = CppTypes.GetCppType(declarationType.Type);
            string typeName;
            if (_writeUpdateMasks)
            {
                var bit = CppTypes.CreateConstantForTemplateParameter(_fieldBitIndex[name][0]);
                if (fieldGeneratedType.IsArray)
                {
                    if (typeof(DynamicUpdateField).IsAssignableFrom(fieldGeneratedType.GetElementType()))
                    {
                        var elementType = PrepareFieldType(fieldGeneratedType.GetElementType().GenericTypeArguments[0]);
                        typeName = TypeHandler.GetFriendlyName(elementType);
                        fieldGeneratedType = _arrayUpdateFieldType.MakeGenericType(
                            _dynamicUpdateFieldType.MakeGenericType(elementType, CppTypes.CreateConstantForTemplateParameter(-1), CppTypes.CreateConstantForTemplateParameter(-1)),
                            CppTypes.CreateConstantForTemplateParameter(declarationType.Size),
                            bit,
                            CppTypes.CreateConstantForTemplateParameter(_fieldBitIndex[name][1]));
                    }
                    else
                    {
                        var elementType = PrepareFieldType(fieldGeneratedType.GetElementType());
                        typeName = TypeHandler.GetFriendlyName(elementType);
                        fieldGeneratedType = _arrayUpdateFieldType.MakeGenericType(elementType,
                            CppTypes.CreateConstantForTemplateParameter(declarationType.Size),
                            bit,
                            CppTypes.CreateConstantForTemplateParameter(_fieldBitIndex[name][1]));
                    }
                }
                else if (typeof(DynamicUpdateField).IsAssignableFrom(declarationType.Type))
                {
                    var elementType = PrepareFieldType(fieldGeneratedType.GenericTypeArguments[0]);
                    typeName = TypeHandler.GetFriendlyName(elementType);
                    fieldGeneratedType = _dynamicUpdateFieldType.MakeGenericType(PrepareFieldType(fieldGeneratedType.GenericTypeArguments[0]),
                        CppTypes.CreateConstantForTemplateParameter(_fieldBitIndex[name][1]),
                        bit);
                }
                else
                {
                    var elementType = PrepareFieldType(declarationType.Type);
                    typeName = TypeHandler.GetFriendlyName(elementType);
                    fieldGeneratedType = _updateFieldType.MakeGenericType(PrepareFieldType(declarationType.Type),
                        CppTypes.CreateConstantForTemplateParameter(_fieldBitIndex[name][1]),
                        bit);
                }

                _header.WriteLine($"    {TypeHandler.GetFriendlyName(fieldGeneratedType)} {name};");
            }
            else if (fieldGeneratedType.IsArray)
            {
                typeName = TypeHandler.GetFriendlyName(fieldGeneratedType.GetElementType());
                _header.WriteLine($"    {typeName} {name}[{declarationType.Size}];");
            }
            else
            {
                typeName = TypeHandler.GetFriendlyName(fieldGeneratedType);
                _header.WriteLine($"    {typeName} {name};");
            }

            if ((declarationType.CustomFlag & CustomUpdateFieldFlag.ViewerDependent) != CustomUpdateFieldFlag.None)
                _header.WriteLine($"    struct {name}Tag : ViewerDependentValueTag<{typeName}> {{}};");
        }

        public override void FinishControlBlocks(IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            _fieldWrites.Add((string.Empty, false, (pcf) =>
            {
                FinishControlBlocks(_source, pcf);
                return new List<FlowControlBlock>();
            }
            ));
        }

        public override void FinishBitPack()
        {
            _fieldWrites.Add((string.Empty, false, (pcf) =>
            {
                _source.WriteLine($"{GetIndent()}data.FlushBits();");
                return new List<FlowControlBlock>();
            }));
        }

        protected override string RenameType(Type type)
        {
            return RenameType(type.Name);
        }

        private string RenameType(string name)
        {
            if (name.StartsWith("CG") && char.IsUpper(name[2]))
                name = name.Substring(2);
            if (name.EndsWith("_C"))
                name = name.Substring(0, name.Length - 2);
            if (name.StartsWith("JamMirror"))
                name = name.Substring(9);
            return name;
        }

        protected override string RenameField(string name)
        {
            name = name.Replace("m_", "");
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private static void WriteLicense(TextWriter writer)
        {
            writer.WriteLine("/*");
            writer.WriteLine($" * Copyright (C) 2008-{DateTime.UtcNow.Year} TrinityCore <https://www.trinitycore.org/>");
            writer.WriteLine(" *");
            writer.WriteLine(" * This program is free software; you can redistribute it and/or modify it");
            writer.WriteLine(" * under the terms of the GNU General Public License as published by the");
            writer.WriteLine(" * Free Software Foundation; either version 2 of the License, or (at your");
            writer.WriteLine(" * option) any later version.");
            writer.WriteLine(" *");
            writer.WriteLine(" * This program is distributed in the hope that it will be useful, but WITHOUT");
            writer.WriteLine(" * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or");
            writer.WriteLine(" * FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for");
            writer.WriteLine(" * more details.");
            writer.WriteLine(" *");
            writer.WriteLine(" * You should have received a copy of the GNU General Public License along");
            writer.WriteLine(" * with this program. If not, see <http://www.gnu.org/licenses/>.");
            writer.WriteLine(" */");
            writer.WriteLine();
        }

        private static string GetClassForObjectType(ObjectType objectType)
        {
            switch (objectType)
            {
                case ObjectType.Object:
                    return "Object";
                case ObjectType.Item:
                    return "Item";
                case ObjectType.Container:
                    return "Bag";
                case ObjectType.AzeriteEmpoweredItem:
                    return "Item";
                case ObjectType.AzeriteItem:
                    return "Item";
                case ObjectType.Unit:
                    return "Unit";
                case ObjectType.Player:
                    return "Player";
                case ObjectType.ActivePlayer:
                    return "Player";
                case ObjectType.GameObject:
                    return "GameObject";
                case ObjectType.DynamicObject:
                    return "DynamicObject";
                case ObjectType.Corpse:
                    return "Corpse";
                case ObjectType.AreaTrigger:
                    return "AreaTrigger";
                case ObjectType.SceneObject:
                    return "Object";
                case ObjectType.Conversation:
                    return "Conversation";
                default:
                    throw new ArgumentOutOfRangeException(nameof(objectType));
            }
        }
    }
}
