﻿using System;
using System.Linq;
using System.Reflection;
using ECSExtension.Patch;
using ECSExtension.Util;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;

#if IL2CPP
using Il2CppInterop.Runtime;
#endif

namespace UniverseLib.Runtime
{
    public static unsafe class ECSHelper
    {
        public static Action<World> WorldCreated;
        public static Action<World> WorldDestroyed;

        internal static void StartListening()
        {
#if IL2CPP
            World.WorldCreated += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<World>>((World world) => WorldCreated?.Invoke(world));
            World.WorldDestroyed += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<World>>((World world) => WorldDestroyed?.Invoke(world));
#else
            World.WorldCreated += world => WorldCreated?.Invoke(world);
            World.WorldDestroyed += world => WorldDestroyed?.Invoke(world);
#endif
        }

        public static int GetModTypeIndex(Type type)
        {
#if IL2CPP
            int index = TypeManager.GetTypeIndex(Il2CppType.From(type));
#else
            int index = TypeManager.GetTypeIndex(type);
#endif
            return index;
        }

        public static int GetModTypeIndex<T>()
        {
            try
            {
                var index = TypeManager.GetTypeIndex<T>();

                if (index <= 0)
                {
                    throw new ArgumentException($"Failed to get type index for {typeof(T).FullName}");
                }

                return index;
            }
            catch (Exception)
            {
#if IL2CPP
                int index = TypeManager.GetTypeIndex(Il2CppType.Of<T>());
#else
                int index = TypeManager.GetTypeIndex(typeof(T));
#endif
                if (index <= 0)
                {
                    throw new ArgumentException($"Failed to get type index for {typeof(T).FullName}");
                }

                return index;
            }
        }

        public const int ClearFlagsMask = 0x007FFFFF;

        public static ref readonly TypeManager.TypeInfo GetTypeInfo(int typeIndex)
        {
            return ref TypeManager.GetTypeInfoPointer()[typeIndex & ClearFlagsMask];
        }

        public static bool ExistsSafe(this EntityManager entityManager, Entity entity)
        {
            try
            {
                return entityManager.Exists(entity);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ComponentType ReadOnly<T>()
        {
            int typeIndex = GetModTypeIndex<T>();
            ComponentType componentType = ComponentType.FromTypeIndex(typeIndex);
            componentType.AccessModeType = ComponentType.AccessMode.ReadOnly;
            return componentType;
        }

        public static void SetEnabled(this EntityManager entityManager, Entity entity, bool enabled)
        {
            if (IsEntityEnabled(entityManager, entity) == enabled)
            {
                return;
            }

            ComponentType componentType = ReadOnly<Disabled>();
            if (entityManager.HasModComponent<LinkedEntityGroup>(entity))
            {
                NativeArray<Entity> entities = entityManager
                    .GetModBuffer<LinkedEntityGroup>(entity)
                    .Reinterpret<Entity>()
                    .ToIl2CppNativeArray(Allocator.TempJob);
                if (enabled)
                {
                    entityManager.RemoveComponent(entities, componentType);
                }
                else
                {
                    entityManager.AddComponent(entities, componentType);
                }

                entities.Dispose();
                return;
            }

            if (!enabled)
            {
                entityManager.AddComponent(entity, componentType);
                return;
            }

            entityManager.RemoveComponent(entity, componentType);
        }

        public static string GetName(EntityManager entityManager, Entity entity)
        {
            if (ECSInitialize.CurrentECSVersion < ECSInitialize.ECSVersion.V0_51)
            {
                return entity.ToString();
            }
            
            return GetName_Internal(entityManager, entity);
        }

        private static string GetName_Internal(EntityManager entityManager, Entity entity)
        {
            string result;
            var method1 = typeof(EntityManager).GetMethod("GetName", AccessTools.all, null, new[] { typeof(Entity) }, null);
            if (method1 != null)
            {
                result = (string)method1.Invoke(entityManager, new object[] { entity });
                return string.IsNullOrEmpty(result) ? entity.ToString() : result;
            }

            var method2 = typeof(EntityManager).GetMethod("GetName", AccessTools.all, null, new[] { typeof(Entity), typeof(FixedString64Bytes).MakeByRefType() }, null);
            if (method2 != null)
            {
                object[] args = { entity, new FixedString64Bytes("") };
                method2.Invoke(entityManager, args);
                result = ((FixedString64Bytes)args[1]).Value;
                return string.IsNullOrEmpty(result) ? entity.ToString() : result;
            }

            return entity.ToString();
        }

        public static void SetName(EntityManager entityManager, Entity entity, string name)
        {
            if (ECSInitialize.CurrentECSVersion < ECSInitialize.ECSVersion.V0_51)
            {
                return;
            }

            SetName_Internal(entityManager, entity, name);
        }

        private static void SetName_Internal(EntityManager entityManager, Entity entity, string name)
        {
            var method1 = typeof(EntityManager).GetMethod("SetName", AccessTools.all, null, new[] { typeof(Entity), typeof(string) }, null);
            if (method1 != null)
            {
                method1.Invoke(entityManager, new object[] { entity, name });
                return;
            }

            var method2 = typeof(EntityManager).GetMethod("SetName", AccessTools.all, null, new[] { typeof(Entity), typeof(FixedString64Bytes) }, null);
            if (method2 != null)
            {
                FixedString64Bytes strBytes = new FixedString64Bytes(name);
                method2.Invoke(entityManager, new object[] { entity, strBytes });
            }
        }

        public static bool IsEntityEnabled(EntityManager entityManager, Entity entity)
        {
            return !entityManager.HasComponent(entity, ReadOnly<Disabled>());
        }

        /// <summary>
        /// Gets the dynamic buffer of an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="isReadOnly">Specify whether the access to the component through this object is read only
        /// or read and write. </param>
        /// <typeparam name="T">The type of the buffer's elements.</typeparam>
        /// <returns>The DynamicBuffer object for accessing the buffer contents.</returns>
        /// <exception cref="ArgumentException">Thrown if T is an unsupported type.</exception>
        public static ModDynamicBuffer<T> GetModBuffer<T>(this EntityManager entityManager, Entity entity, bool isReadOnly = false) where T : unmanaged
        {
            var typeIndex = GetModTypeIndex<T>();
            var access = entityManager.GetCheckedEntityDataAccess();

            if (!access->IsInExclusiveTransaction)
            {
                if (isReadOnly)
                    access->DependencyManager->CompleteWriteDependency(typeIndex);
                else
                    access->DependencyManager->CompleteReadAndWriteDependency(typeIndex);
            }

            BufferHeader* header;
            if (isReadOnly)
            {
                header = (BufferHeader*)access->EntityComponentStore->GetComponentDataWithTypeRO(entity, typeIndex);
            }
            else
            {
                header = (BufferHeader*)access->EntityComponentStore->GetComponentDataWithTypeRW(entity, typeIndex,
                    access->EntityComponentStore->GlobalSystemVersion);
            }

            int internalCapacity = GetTypeInfo(typeIndex).BufferCapacity;
            return new ModDynamicBuffer<T>(header, internalCapacity);
        }

        public static T GetModComponentData<T>(this EntityManager entityManager, Entity entity)
        {
            int typeIndex = GetModTypeIndex<T>();
            var dataAccess = entityManager.GetCheckedEntityDataAccess();

            if (!dataAccess->HasComponent(entity, ComponentType.FromTypeIndex(typeIndex)))
            {
                throw new InvalidOperationException($"Tried to get component data for component {typeof(T).FullName}, which entity does not have!");
            }

            if (!dataAccess->IsInExclusiveTransaction)
            {
                (&dataAccess->m_DependencyManager)->CompleteWriteDependency(typeIndex);
            }

            byte* ret = dataAccess->EntityComponentStore->GetComponentDataWithTypeRO(entity, typeIndex);

            return ModUnsafe.Read<T>(ret);
        }

        /// <summary>
        /// Set Component Data of type.
        /// This method will work on any type, including mod created ones
        /// </summary>
        /// <param name="entity">Target Entity</param>
        /// <param name="entityManager">World EntityManager</param>
        /// <param name="component">Component Data</param>
        /// <typeparam name="T">Component Type</typeparam>
        public static void SetModComponentData<T>(this EntityManager entityManager, Entity entity, T component)
        {
            int typeIndex = GetModTypeIndex<T>();
            var dataAccess = entityManager.GetCheckedEntityDataAccess();
            var componentStore = dataAccess->EntityComponentStore;

            if (!dataAccess->HasComponent(entity, ComponentType.FromTypeIndex(typeIndex)))
            {
                throw new InvalidOperationException($"Tried to set component data for component {typeof(T).FullName}, which entity does not have!");
            }

            if (!dataAccess->IsInExclusiveTransaction)
            {
                (&dataAccess->m_DependencyManager)->CompleteReadAndWriteDependency(typeIndex);
            }

            byte* writePtr = componentStore->GetComponentDataWithTypeRW(entity, typeIndex, componentStore->m_GlobalSystemVersion);
            ModUnsafe.Copy(writePtr, ref component);
        }

        public static bool HasModComponent<T>(this EntityManager entityManager, Entity entity)
        {
            ComponentType componentType = ComponentType.FromTypeIndex(GetModTypeIndex<T>());
            var dataAccess = entityManager.GetCheckedEntityDataAccess();

            return dataAccess->HasComponent(entity, componentType);
        }
    }
}