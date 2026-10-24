// Copyright (c) 2026 OopsItsACoder
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UJect.Exceptions;

namespace UJect.Injection
{
    /// <summary>
    /// Cached reflection and dependency information for a specific type
    /// </summary>
    internal class Injector
    {
        private static readonly Type injectAttributeType = typeof(InjectAttribute);
        private static readonly Type injectAllAttributeType = typeof(InjectAllAttribute);

        private const BindingFlags INJECTABLE_BINDING_FLAGS
            = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly HashSet<InjectionKey>       dependsOn              = new();
        private readonly List<InjectableConstructor> injectableConstructors = new();
        private readonly List<InjectableField>       injectableFields       = new();
        private readonly Type                        referencedType;

        public IReadOnlyList<InjectableConstructor> InjectableConstructors => injectableConstructors;
        public IReadOnlyList<InjectableField> InjectableFields => injectableFields;

        public Injector(Type objType)
        {
            referencedType = objType;
            FetchFields();
            FetchConstructors();
        }

        /// <summary>
        /// All dependencies of the contained type, as determined by fields and constructors
        /// </summary>
        public IReadOnlyCollection<InjectionKey> DependsOn => dependsOn;

        private static void GetInjectableFieldInfosForDeclaringType(Type t, List<FieldInfo> scratchFieldInfo)
        {
            scratchFieldInfo.Clear();
            foreach (var fi in t.GetFields(INJECTABLE_BINDING_FLAGS))
            {
                if (fi.DeclaringType != t) continue;
                if (!fi.IsDefined(injectAttributeType, true)) continue;
                scratchFieldInfo.Add(fi);
            }
        }

        [ThreadStatic]
        private static List<FieldInfo>? _scratchFieldInfo;

        private static List<FieldInfo> ScratchFieldInfo
        {
            get
            {
                if (_scratchFieldInfo == null) _scratchFieldInfo = new();
                return _scratchFieldInfo;
            }
        }

        private void FetchFields()
        {
            injectableFields.Clear();
            
            // Grab all fields with proper binding flags and an inject attribute
            GetInjectableFieldInfosForDeclaringType(referencedType, ScratchFieldInfo);

            foreach (var fieldInfo in ScratchFieldInfo)
            {
                var customId = fieldInfo.GetCustomAttribute<InjectAttribute>(true).CustomId;
                var fieldInjectionKey = new InjectionKey(fieldInfo.FieldType, customId);
                
                // mark the dependency for later use in the dependency tree
                dependsOn.Add(fieldInjectionKey);
                
                // Add the field
                injectableFields.Add(new InjectableField(fieldInfo, fieldInjectionKey));
            }

            var baseType = referencedType.BaseType;
            while (baseType != null && baseType != typeof(System.Object))
            {
                // If there's an injector for the base type, use that, it's fastest
                if (InjectorCache.TryGetInjector(baseType, out var baseTypeInjector))
                {
                    injectableFields.AddRange(baseTypeInjector.InjectableFields);
                    dependsOn.UnionWith(baseTypeInjector.dependsOn);
                    break;
                }
                
                // Otherwise, see if there's any injectable fields in the base type
                GetInjectableFieldInfosForDeclaringType(baseType, ScratchFieldInfo);
                if (ScratchFieldInfo.Count  > 0)
                {
                    // If there's any injectable fields, cache an injector for the base type.
                    // This'll speed up subsequent requests assuming multiple subclasses of one base class
                    // as they'll hit the cache on the second pass
                    baseTypeInjector = InjectorCache.GetOrCreateInjector(baseType);
                    dependsOn.UnionWith(baseTypeInjector.dependsOn);
                    injectableFields.AddRange(baseTypeInjector.InjectableFields);
                    break;
                }

                baseType = baseType.BaseType;
            }
        }

        private void FetchConstructors()
        {
            injectableConstructors.Clear();
            var constructors = referencedType.GetConstructors(INJECTABLE_BINDING_FLAGS);
            foreach (var constructorInfo in constructors)
            {
                var parameterInfos = constructorInfo.GetParameters();

                var isInjectableConstructor = constructorInfo.IsDefined(injectAllAttributeType, true);
                if (!isInjectableConstructor)
                {
                    isInjectableConstructor = parameterInfos.All(pi => pi.IsDefined(injectAttributeType, true));
                }
                
                if (isInjectableConstructor)
                {
                    var argsKeys = new InjectionKey[parameterInfos.Length];
                    for (var paramIndex = 0; paramIndex < parameterInfos.Length; paramIndex++)
                    {
                        var parameterInfo = parameterInfos[paramIndex];
                        var customId = parameterInfo.GetCustomAttribute<InjectAttribute>(true)?.CustomId;
                        var argKey = new InjectionKey(parameterInfo.ParameterType, customId);
                        dependsOn.Add(argKey);
                        argsKeys[paramIndex] = argKey;
                    }

                    var injectableConstructor = new InjectableConstructor(constructorInfo, argsKeys);
                    injectableConstructors.Add(injectableConstructor);
                }
            }

            // Sort constructors by most params to least, so we always fill out as much data as possible
            injectableConstructors.Sort((c1, c2) => c2.ParamKeys.Length.CompareTo(c1.ParamKeys.Length));
        }

        public void InjectFields(object obj, DiContainer diContainer)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj), "Cannot inject into null object");
            
            foreach (var injectableField in injectableFields)
            {
                if (diContainer.TryGetDependencyInternal<object>(injectableField.InjectionKey, out var dependency))
                {
                    injectableField.FieldInfo.SetValue(obj, dependency);
                }
                else
                {
                    throw new InjectionException(obj.GetType(), $"No dependency found for injected field {injectableField.FieldInfo.Name} with key {injectableField.InjectionKey} in {obj}");
                }
            }
        }

        public object CreateInstance(DiContainer diContainer, Type newInstanceType)
        {
            if (injectableConstructors.Count == 0)
            {
                throw new InjectionException(newInstanceType, "No constructor found");
            }
            
            var constructorPair = injectableConstructors.First();
            var paramKeys = constructorPair.ParamKeys;
            var args = new object[paramKeys.Length];
            for (var paramIndex = 0; paramIndex < paramKeys.Length; paramIndex++)
            {
                var argKey = paramKeys[paramIndex];
                if (diContainer.TryGetDependencyInternal<object>(argKey, out var dep))
                {
                    args[paramIndex] = dep;
                }
                else
                {
                    throw new InjectionException(newInstanceType, $"Missing dependency for object constructor - parameter {paramIndex} of type {argKey.InjectedResourceType}");
                }
            }

            var instance = constructorPair.ConstructorInfo.Invoke(args);
            
            // We don't call InjectFields here because it'll be called automatically when the instance resolves
            
            return instance;
        }

        public TImpl CreateInstance<TImpl>(DiContainer diContainer) => (TImpl)CreateInstance(diContainer, typeof(TImpl));

        #region Helper Structs

        internal struct InjectableConstructor
        {
            public readonly ConstructorInfo ConstructorInfo;
            public readonly InjectionKey[]  ParamKeys;

            public InjectableConstructor(ConstructorInfo constructorInfo, InjectionKey[] paramKeys)
            {
                ConstructorInfo = constructorInfo;
                ParamKeys       = paramKeys;
            }
        }

        internal struct InjectableField
        {
            public readonly FieldInfo    FieldInfo;
            public readonly InjectionKey InjectionKey;

            public InjectableField(FieldInfo fieldInfo, InjectionKey injectionKey)
            {
                FieldInfo    = fieldInfo;
                InjectionKey = injectionKey;
            }
        }

        #endregion
    }
}
