﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ZeroFormatter.Formatters;
using ZeroFormatter.Internal;

namespace ZeroFormatter.Segments
{
    // ObjectSegment is inherit to target class directly or generate.

    // Layout: [int byteSize][indexCount][indexOffset:int...][t format...]

    public static class ObjectSegmentHelper
    {
        // Dirty Helpers...

        public static int GetByteSize(ArraySegment<byte> originalBytes)
        {
            var array = originalBytes.Array;
            return BinaryUtil.ReadInt32(ref array, originalBytes.Offset);
        }

        public static int GetOffset(ArraySegment<byte> originalBytes, int index, int lastIndex)
        {
            if (index > lastIndex)
            {
                return -1;
            }
            var array = originalBytes.Array;
            return BinaryUtil.ReadInt32(ref array, originalBytes.Offset + 8 + 4 * index);
        }

        public static ArraySegment<byte> GetSegment(ArraySegment<byte> originalBytes, int index, int lastIndex)
        {
            if (index > lastIndex)
            {
                return default(ArraySegment<byte>); // note:very very dangerous.
            }

            var offset = GetOffset(originalBytes, index, lastIndex);
            if (index == lastIndex)
            {
                var sliceLength = originalBytes.Offset + originalBytes.Count;
                return new ArraySegment<byte>(originalBytes.Array, offset, (sliceLength - offset));
            }
            else
            {
                var nextIndex = index + 1;
                do
                {
                    var nextOffset = GetOffset(originalBytes, nextIndex, lastIndex);
                    if (nextOffset != 0)
                    {
                        return new ArraySegment<byte>(originalBytes.Array, offset, (nextOffset - offset));
                    }
                } while (nextIndex++ < lastIndex); // if reached over the lastIndex, ge


                var sliceLength = originalBytes.Offset + originalBytes.Count;
                return new ArraySegment<byte>(originalBytes.Array, offset, (sliceLength - offset));
            }
        }

        public static int SerializeFixedLength<T>(ref byte[] targetBytes, int startOffset, int offset, int index, int lastIndex, ArraySegment<byte> originalBytes, byte[] extraBytes)
        {
            BinaryUtil.WriteInt32(ref targetBytes, startOffset + (8 + 4 * index), offset);

            var formatter = global::ZeroFormatter.Formatters.Formatter<T>.Default;
            var len = formatter.GetLength();

            BinaryUtil.EnsureCapacity(ref targetBytes, offset, len.Value);
            var readOffset = GetOffset(originalBytes, index, lastIndex);
            if (readOffset != -1)
            {
                Buffer.BlockCopy(originalBytes.Array, readOffset, targetBytes, offset, len.Value);
            }
            else
            {
                var extraOffset = GetExtraBytesOffset(extraBytes, lastIndex, index);
                Buffer.BlockCopy(extraBytes, extraOffset, targetBytes, offset, len.Value);
            }

            return len.Value;
        }

        public static int SerializeSegment<T>(ref byte[] targetBytes, int startOffset, int offset, int index, T segment)
        {
            BinaryUtil.WriteInt32(ref targetBytes, startOffset + (8 + 4 * index), offset);

            var formatter = global::ZeroFormatter.Formatters.Formatter<T>.Default;
            return formatter.Serialize(ref targetBytes, offset, segment);
        }

        public static int SerializeCacheSegment<T>(ref byte[] targetBytes, int startOffset, int offset, int index, CacheSegment<T> segment)
        {
            BinaryUtil.WriteInt32(ref targetBytes, startOffset + (8 + 4 * index), offset);

            return segment.Serialize(ref targetBytes, offset);
        }

        public static T GetFixedProperty<T>(ArraySegment<byte> bytes, int index, int lastIndex, byte[] extraBytes, DirtyTracker tracker)
        {
            if (index <= lastIndex)
            {
                var array = bytes.Array;
                int _;
                return Formatter<T>.Default.Deserialize(ref array, ObjectSegmentHelper.GetOffset(bytes, index, lastIndex), tracker, out _);
            }
            else
            {
                // from extraBytes
                var offset = GetExtraBytesOffset(extraBytes, lastIndex, index);
                int _;
                return Formatter<T>.Default.Deserialize(ref extraBytes, offset, tracker, out _);
            }
        }

        public static void SetFixedProperty<T>(ArraySegment<byte> bytes, int index, int lastIndex, byte[] extraBytes, T value)
        {
            if (index <= lastIndex)
            {
                var array = bytes.Array;
                Formatter<T>.Default.Serialize(ref array, ObjectSegmentHelper.GetOffset(bytes, index, lastIndex), value);
            }
            else
            {
                // from extraBytes
                var offset = GetExtraBytesOffset(extraBytes, lastIndex, index);
                Formatter<T>.Default.Serialize(ref extraBytes, offset, value);
            }
        }

        public static int WriteSize(ref byte[] targetBytes, int startOffset, int lastOffset, int lastIndex)
        {
            BinaryUtil.WriteInt32(ref targetBytes, startOffset + 4, lastIndex);
            var writeSize = lastOffset - startOffset;
            BinaryUtil.WriteInt32(ref targetBytes, startOffset, writeSize);
            return writeSize;
        }

        public static int DirectCopyAll(ArraySegment<byte> originalBytes, ref byte[] targetBytes, int targetOffset)
        {
            var array = originalBytes.Array;
            var copyCount = BinaryUtil.ReadInt32(ref array, originalBytes.Offset);
            BinaryUtil.EnsureCapacity(ref targetBytes, targetOffset, copyCount);
            Buffer.BlockCopy(array, originalBytes.Offset, targetBytes, targetOffset, copyCount);
            return copyCount;
        }

        static int GetExtraBytesOffset(byte[] extraBytes, int binaryLastIndex, int index)
        {
            var offset = (index - binaryLastIndex - 1) * 4;
            return BinaryUtil.ReadInt32(ref extraBytes, offset);
        }

        public static byte[] CreateExtraFixedBytes(int binaryLastIndex, int schemaLastIndex, int[] elementSizes)
        {
            if (binaryLastIndex < schemaLastIndex)
            {
                // [header offsets] + [elements]
                var headerLength = (schemaLastIndex - binaryLastIndex) * 4;
                var elementSizeSum = elementSizes.Sum();
                var bytes = new byte[headerLength + elementSizeSum];
                var offset = headerLength + 4;
                for (int i = (binaryLastIndex + 1); i < elementSizes.Length; i++)
                {
                    if (elementSizes[i] != 0)
                    {
                        BinaryUtil.WriteInt32(ref bytes, (i - binaryLastIndex - 1) * 4, offset);
                        offset += elementSizes[i];
                    }
                }

                return bytes;
            }
            else
            {
                return null;
            }
        }

        public static int SerialzieFromFormatter<T>(ref byte[] bytes, int startOffset, int offset, int index, T value)
        {
            BinaryUtil.WriteInt32(ref bytes, startOffset + (8 + 4 * index), offset);
            return Formatter<T>.Default.Serialize(ref bytes, offset, value);
        }
    }

    internal static class DynamicAssemblyHolder
    {
        public const string ModuleName = "ZeroFormatter.DynamicObjectSegments";

        public static readonly Lazy<ModuleBuilder> Module = new Lazy<ModuleBuilder>(() => MakeDynamicAssembly(), true);

        public static object ModuleLock = new object(); // be careful!

        static ModuleBuilder MakeDynamicAssembly()
        {
            var assemblyName = new AssemblyName(ModuleName);
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(ModuleName);

            return moduleBuilder;
        }
    }

    internal static class DynamicObjectSegmentBuilder<T>
    {
        static readonly MethodInfo ArraySegmentArrayGet = typeof(ArraySegment<byte>).GetProperty("Array").GetGetMethod();
        static readonly MethodInfo ArraySegmentOffsetGet = typeof(ArraySegment<byte>).GetProperty("Offset").GetGetMethod();
        static readonly MethodInfo ReadInt32 = typeof(BinaryUtil).GetMethod("ReadInt32");
        static readonly MethodInfo CreateChild = typeof(DirtyTracker).GetMethod("CreateChild");
        static readonly MethodInfo Dirty = typeof(DirtyTracker).GetMethod("Dirty");
        static readonly MethodInfo IsDirty = typeof(DirtyTracker).GetProperty("IsDirty").GetGetMethod();
        static readonly MethodInfo GetSegment = typeof(ObjectSegmentHelper).GetMethod("GetSegment");
        static readonly MethodInfo GetOffset = typeof(ObjectSegmentHelper).GetMethod("GetOffset");

        static readonly Lazy<Type> lazyBuild = new Lazy<Type>(() => Build(), true);

        class PropertyTuple
        {
            public int Index;
            public PropertyInfo PropertyInfo;
            public FieldInfo SegmentField;
            public bool IsCacheSegment;
            public bool IsFixedSize;
            public int FixedSize;
        }

        public static Type GetProxyType()
        {
            return lazyBuild.Value;
        }

        static Type Build()
        {
            Type generatedType;
            lock (DynamicAssemblyHolder.ModuleLock)
            {
                var moduleBuilder = DynamicAssemblyHolder.Module.Value;
                generatedType = GenerateObjectSegmentImplementation(moduleBuilder);
            }
            return generatedType;
        }

        static Type GenerateObjectSegmentImplementation(ModuleBuilder moduleBuilder)
        {
            // public class DynamicObjectSegment.MyClass : MyClass, IZeroFormatterSegment
            var type = moduleBuilder.DefineType(
                DynamicAssemblyHolder.ModuleName + "." + typeof(T).FullName,
                TypeAttributes.Public,
                typeof(T));

            var originalBytes = type.DefineField("<>_originalBytes", typeof(ArraySegment<byte>), FieldAttributes.Private | FieldAttributes.InitOnly);
            var tracker = type.DefineField("<>_tracker", typeof(DirtyTracker), FieldAttributes.Private | FieldAttributes.InitOnly);
            var binaryLastIndex = type.DefineField("<>_binaryLastIndex", typeof(int), FieldAttributes.Private | FieldAttributes.InitOnly);
            var extraFixedBytes = type.DefineField("<>_extraFixedBytes", typeof(byte[]), FieldAttributes.Private | FieldAttributes.InitOnly);

            var properties = GetPropertiesWithVerify(type);

            BuildConstructor(type, originalBytes, tracker, binaryLastIndex, extraFixedBytes, properties);

            foreach (var item in properties)
            {
                if (item.IsFixedSize)
                {
                    BuildFixedProperty(type, originalBytes, tracker, binaryLastIndex, extraFixedBytes, item);
                }
                else if (item.IsCacheSegment)
                {
                    BuildCacheSegmentProperty(type, originalBytes, tracker, item);
                }
                else
                {
                    BuildSegmentProperty(type, originalBytes, tracker, item);
                }
            }

            BuildInterfaceMethod(type, originalBytes, tracker, binaryLastIndex, extraFixedBytes, properties);

            return type.CreateType();
        }

        static PropertyTuple[] GetPropertiesWithVerify(TypeBuilder typeBuilder)
        {
            var lastIndex = -1;

            var list = new List<PropertyTuple>();

            // getproperties contains verify.
            foreach (var p in DynamicObjectDescriptor.GetProperties(typeof(T)))
            {
                var propInfo = p.Item2;
                var index = p.Item1;

                var formatter = (IFormatter)typeof(Formatter<>).MakeGenericType(propInfo.PropertyType).GetProperty("Default").GetValue(null, Type.EmptyTypes);

                if (formatter.GetLength() == null)
                {
                    if (CacheSegment.CanAccept(propInfo.PropertyType))
                    {
                        var fieldBuilder = typeBuilder.DefineField("<>_" + propInfo.Name, typeof(CacheSegment<>).MakeGenericType(propInfo.PropertyType), FieldAttributes.Private);
                        list.Add(new PropertyTuple { Index = index, PropertyInfo = propInfo, IsFixedSize = false, SegmentField = fieldBuilder, IsCacheSegment = true });
                    }
                    else
                    {
                        var fieldBuilder = typeBuilder.DefineField("<>_" + propInfo.Name, propInfo.PropertyType, FieldAttributes.Private);
                        list.Add(new PropertyTuple { Index = index, PropertyInfo = propInfo, IsFixedSize = false, SegmentField = fieldBuilder });
                    }
                }
                else
                {
                    list.Add(new PropertyTuple { Index = index, PropertyInfo = propInfo, IsFixedSize = true, FixedSize = formatter.GetLength().Value });
                }

                lastIndex = index;
            }

            return list.OrderBy(x => x.Index).ToArray();
        }

        static void BuildConstructor(TypeBuilder type, FieldInfo originalBytesField, FieldInfo trackerField, FieldInfo lastIndexField, FieldInfo extraFixedBytes, PropertyTuple[] properties)
        {
            var method = type.DefineMethod(".ctor", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.HideBySig);

            var baseCtor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { }, null);

            method.SetReturnType(typeof(void));
            method.SetParameters(typeof(DirtyTracker), typeof(ArraySegment<byte>));

            var generator = method.GetILGenerator();

            generator.DeclareLocal(typeof(byte[]));
            generator.DeclareLocal(typeof(int));
            generator.DeclareLocal(typeof(int[]));

            // ctor
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, baseCtor);

            // Local Var( var array = originalBytes.Array; )
            generator.Emit(OpCodes.Ldarga_S, (byte)2);
            generator.Emit(OpCodes.Call, ArraySegmentArrayGet);
            generator.Emit(OpCodes.Stloc_0);

            // Assign Field Common
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Stfld, originalBytesField);

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Callvirt, CreateChild);
            generator.Emit(OpCodes.Dup);
            generator.Emit(OpCodes.Starg_S, (byte)1);
            generator.Emit(OpCodes.Stfld, trackerField);

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldloca_S, (byte)0);
            generator.Emit(OpCodes.Ldarga_S, (byte)2);
            generator.Emit(OpCodes.Call, ArraySegmentOffsetGet);
            generator.Emit(OpCodes.Ldc_I4_4);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Call, ReadInt32);
            generator.Emit(OpCodes.Stfld, lastIndexField);

            var schemaLastIndex = properties.Select(x => x.Index).LastOrDefault();
            var dict = properties.Where(x => x.IsFixedSize).ToDictionary(x => x.Index, x => x.FixedSize);
            var elementSizes = new int[schemaLastIndex + 1];
            for (int i = 0; i < schemaLastIndex + 1; i++)
            {
                if (!dict.TryGetValue(i, out elementSizes[i]))
                {
                    elementSizes[i] = 0;
                }
            }
            EmitNewArray(generator, elementSizes);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, lastIndexField);
            generator.Emit(OpCodes.Ldc_I4, schemaLastIndex);
            generator.Emit(OpCodes.Ldloc_2);
            generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("CreateExtraFixedBytes"));
            generator.Emit(OpCodes.Stfld, extraFixedBytes);

            foreach (var item in properties)
            {
                if (item.IsFixedSize) continue;

                if (item.IsCacheSegment)
                {
                    AssignCacheSegment(generator, item.Index, trackerField, lastIndexField, item.SegmentField);
                }
                else
                {
                    AssignSegment(generator, item.Index, trackerField, lastIndexField, item.SegmentField);
                }
            }

            generator.Emit(OpCodes.Ret);
        }

        static void EmitNewArray(ILGenerator generator, int[] array)
        {
            generator.Emit(OpCodes.Ldc_I4, array.Length);
            generator.Emit(OpCodes.Newarr, typeof(int));
            generator.Emit(OpCodes.Stloc_2);
            for (int i = 0; i < array.Length; i++)
            {
                generator.Emit(OpCodes.Ldloc_2);
                generator.Emit(OpCodes.Ldc_I4, i);
                generator.Emit(OpCodes.Ldc_I4, array[i]);
                generator.Emit(OpCodes.Stelem_I4);
            }
        }

        static void AssignCacheSegment(ILGenerator generator, int index, FieldInfo tracker, FieldInfo lastIndex, FieldInfo field)
        {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, tracker);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Ldc_I4, index);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, lastIndex);
            generator.Emit(OpCodes.Call, GetSegment);
            generator.Emit(OpCodes.Newobj, field.FieldType.GetConstructors().First());
            generator.Emit(OpCodes.Stfld, field);
        }

        static void AssignSegment(ILGenerator generator, int index, FieldInfo tracker, FieldInfo lastIndex, FieldInfo field)
        {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, typeof(Formatter<>).MakeGenericType(field.FieldType).GetProperty("Default").GetGetMethod());
            generator.Emit(OpCodes.Ldloca_S, (byte)0);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Ldc_I4, index);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, lastIndex);
            generator.Emit(OpCodes.Call, GetOffset);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, tracker);
            generator.Emit(OpCodes.Ldloca_S, (byte)1);
            generator.Emit(OpCodes.Callvirt, typeof(Formatter<>).MakeGenericType(field.FieldType).GetMethod("Deserialize"));
            generator.Emit(OpCodes.Stfld, field);
        }

        static void BuildFixedProperty(TypeBuilder type, FieldInfo originalBytesField, FieldInfo trackerField, FieldInfo binaryLastIndex, FieldInfo extraBytes, PropertyTuple property)
        {
            var prop = type.DefineProperty(property.PropertyInfo.Name, property.PropertyInfo.Attributes, property.PropertyInfo.PropertyType, Type.EmptyTypes);

            // build get
            var getMethod = property.PropertyInfo.GetGetMethod();
            if (getMethod != null)
            {
                var method = type.DefineMethod(getMethod.Name, getMethod.Attributes & ~MethodAttributes.NewSlot, getMethod.ReturnType, Type.EmptyTypes);

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, originalBytesField);
                generator.Emit(OpCodes.Ldc_I4, property.Index);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, binaryLastIndex);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, extraBytes);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, trackerField);
                generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("GetFixedProperty").MakeGenericMethod(getMethod.ReturnType));
                generator.Emit(OpCodes.Ret);

                prop.SetGetMethod(method);
            }

            // build set
            var setMethod = property.PropertyInfo.GetSetMethod();
            if (setMethod != null)
            {
                var method = type.DefineMethod(setMethod.Name, setMethod.Attributes & ~MethodAttributes.NewSlot, null, new[] { setMethod.GetParameters()[0].ParameterType });

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, originalBytesField);
                generator.Emit(OpCodes.Ldc_I4, property.Index);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, binaryLastIndex);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, extraBytes);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("SetFixedProperty").MakeGenericMethod(getMethod.ReturnType));
                generator.Emit(OpCodes.Ret);

                prop.SetSetMethod(method);
            }
        }

        static void BuildCacheSegmentProperty(TypeBuilder type, FieldInfo originalBytesField, FieldInfo trackerField, PropertyTuple property)
        {
            var prop = type.DefineProperty(property.PropertyInfo.Name, property.PropertyInfo.Attributes, property.PropertyInfo.PropertyType, Type.EmptyTypes);

            // build get
            var getMethod = property.PropertyInfo.GetGetMethod();
            if (getMethod != null)
            {
                var method = type.DefineMethod(getMethod.Name, getMethod.Attributes & ~MethodAttributes.NewSlot, getMethod.ReturnType, Type.EmptyTypes);

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, property.SegmentField);
                generator.Emit(OpCodes.Callvirt, property.SegmentField.FieldType.GetProperty("Value").GetGetMethod());
                generator.Emit(OpCodes.Ret);

                prop.SetGetMethod(method);
            }

            // build set
            var setMethod = property.PropertyInfo.GetSetMethod();
            if (setMethod != null)
            {
                var method = type.DefineMethod(setMethod.Name, setMethod.Attributes & ~MethodAttributes.NewSlot, null, new[] { setMethod.GetParameters()[0].ParameterType });

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, property.SegmentField);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Callvirt, property.SegmentField.FieldType.GetProperty("Value").GetSetMethod());
                generator.Emit(OpCodes.Ret);

                prop.SetSetMethod(method);
            }
        }

        static void BuildSegmentProperty(TypeBuilder type, FieldInfo originalBytesField, FieldInfo trackerField, PropertyTuple property)
        {
            var prop = type.DefineProperty(property.PropertyInfo.Name, property.PropertyInfo.Attributes, property.PropertyInfo.PropertyType, Type.EmptyTypes);

            // build get
            var getMethod = property.PropertyInfo.GetGetMethod();
            if (getMethod != null)
            {
                var method = type.DefineMethod(getMethod.Name, getMethod.Attributes & ~MethodAttributes.NewSlot, getMethod.ReturnType, Type.EmptyTypes);

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, property.SegmentField);
                generator.Emit(OpCodes.Ret);

                prop.SetGetMethod(method);
            }

            // build set
            var setMethod = property.PropertyInfo.GetSetMethod();
            if (setMethod != null)
            {
                var method = type.DefineMethod(setMethod.Name, setMethod.Attributes & ~MethodAttributes.NewSlot, null, new[] { setMethod.GetParameters()[0].ParameterType });

                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, trackerField);
                generator.Emit(OpCodes.Callvirt, Dirty);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Stfld, property.SegmentField);
                generator.Emit(OpCodes.Ret);

                prop.SetSetMethod(method);
            }
        }

        static void BuildInterfaceMethod(TypeBuilder type, FieldInfo originalBytesField, FieldInfo trackerField, FieldInfo binaryLastIndexField, FieldInfo extraBytes, PropertyTuple[] properties)
        {
            type.AddInterfaceImplementation(typeof(IZeroFormatterSegment));
            // CanDirectCopy
            {
                var method = type.DefineMethod("CanDirectCopy", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual, typeof(bool), Type.EmptyTypes);
                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, trackerField);
                generator.Emit(OpCodes.Callvirt, IsDirty);
                generator.Emit(OpCodes.Ldc_I4_0);
                generator.Emit(OpCodes.Ceq);
                generator.Emit(OpCodes.Ret);
            }
            // GetBufferReference
            {
                var method = type.DefineMethod("GetBufferReference", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual, typeof(ArraySegment<byte>), Type.EmptyTypes);
                var generator = method.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, originalBytesField);
                generator.Emit(OpCodes.Ret);
            }
            // Serialize
            {
                var method = type.DefineMethod("Serialize", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual, typeof(int), new[] { typeof(byte[]).MakeByRefType(), typeof(int) });
                var generator = method.GetILGenerator();

                generator.DeclareLocal(typeof(int));
                var labelA = generator.DefineLabel();
                var labelB = generator.DefineLabel();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, extraBytes);
                generator.Emit(OpCodes.Brtrue, labelA);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, trackerField);
                generator.Emit(OpCodes.Callvirt, IsDirty);
                generator.Emit(OpCodes.Brfalse, labelB);
                // if
                {
                    var schemaLastIndex = properties.Select(x => x.Index).LastOrDefault();
                    var calcedBeginOffset = 8 + (4 * (schemaLastIndex + 1));

                    generator.MarkLabel(labelA);
                    generator.Emit(OpCodes.Ldarg_2);
                    generator.Emit(OpCodes.Stloc_0); // startOffset
                    generator.Emit(OpCodes.Ldarg_2);
                    generator.Emit(OpCodes.Ldc_I4, calcedBeginOffset);
                    generator.Emit(OpCodes.Add);

                    foreach (var prop in properties)
                    {
                        generator.Emit(OpCodes.Starg_S, (byte)2);
                        generator.Emit(OpCodes.Ldarg_2);
                        generator.Emit(OpCodes.Ldarg_1);
                        generator.Emit(OpCodes.Ldloc_0);
                        generator.Emit(OpCodes.Ldarg_2);
                        generator.Emit(OpCodes.Ldc_I4, prop.Index);

                        // load field and call
                        if (prop.IsFixedSize)
                        {
                            generator.Emit(OpCodes.Ldarg_0);
                            generator.Emit(OpCodes.Ldfld, binaryLastIndexField);
                            generator.Emit(OpCodes.Ldarg_0);
                            generator.Emit(OpCodes.Ldfld, originalBytesField);
                            generator.Emit(OpCodes.Ldarg_0);
                            generator.Emit(OpCodes.Ldfld, extraBytes);
                            generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("SerializeFixedLength").MakeGenericMethod(prop.PropertyInfo.PropertyType));
                        }
                        else if (prop.IsCacheSegment)
                        {
                            generator.Emit(OpCodes.Ldarg_0);
                            generator.Emit(OpCodes.Ldfld, prop.SegmentField);
                            generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("SerializeCacheSegment").MakeGenericMethod(prop.PropertyInfo.PropertyType));
                        }
                        else
                        {
                            generator.Emit(OpCodes.Ldarg_0);
                            generator.Emit(OpCodes.Ldfld, prop.SegmentField);
                            generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("SerializeSegment").MakeGenericMethod(prop.PropertyInfo.PropertyType));
                        }
                        generator.Emit(OpCodes.Add);
                    }

                    generator.Emit(OpCodes.Starg_S, (byte)2);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldloc_0);
                    generator.Emit(OpCodes.Ldarg_2);
                    generator.Emit(OpCodes.Ldc_I4, schemaLastIndex);
                    generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("WriteSize"));
                    generator.Emit(OpCodes.Ret);
                }
                // else
                {
                    generator.MarkLabel(labelB);
                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldfld, originalBytesField);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    generator.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetMethod("DirectCopyAll"));
                    generator.Emit(OpCodes.Ret);
                }
            }
        }
    }
}
