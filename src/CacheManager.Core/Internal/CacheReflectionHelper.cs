﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CacheManager.Core.Logging;
using static CacheManager.Core.Utility.Guard;

namespace CacheManager.Core.Internal
{
    internal static class CacheReflectionHelper
    {
        internal static ILoggerFactory CreateLoggerFactory(CacheManagerConfiguration configuration)
        {
            NotNull(configuration, nameof(configuration));

            if (configuration.LoggerFactoryType == null)
            {
                return new NullLoggerFactory();
            }

            CheckImplements<ILoggerFactory>(configuration.LoggerFactoryType);

            var args = new object[] { configuration };
            if (configuration.LoggerFactoryTypeArguments != null)
            {
                args = configuration.LoggerFactoryTypeArguments.Concat(args).ToArray();
            }

            return (ILoggerFactory)CreateInstance(configuration.LoggerFactoryType, args);
        }

        internal static ICacheSerializer CreateSerializer(CacheManagerConfiguration configuration, ILoggerFactory loggerFactory)
        {
            NotNull(configuration, nameof(configuration));
            NotNull(loggerFactory, nameof(loggerFactory));

#if !NETSTANDARD
            if (configuration.SerializerType == null)
            {
                return new BinaryCacheSerializer();
            }
#endif
            if (configuration.SerializerType != null)
            {
                CheckImplements<ICacheSerializer>(configuration.SerializerType);

                var args = new object[] { configuration, loggerFactory };
                if (configuration.SerializerTypeArguments != null)
                {
                    args = configuration.SerializerTypeArguments.Concat(args).ToArray();
                }

                return (ICacheSerializer)CreateInstance(configuration.SerializerType, args);
            }

            return null;
        }

        internal static CacheBackplane CreateBackplane(CacheManagerConfiguration configuration, ILoggerFactory loggerFactory)
        {
            NotNull(configuration, nameof(configuration));
            NotNull(loggerFactory, nameof(loggerFactory));

            if (configuration.BackplaneType != null)
            {
                if (!configuration.CacheHandleConfigurations.Any(p => p.IsBackplaneSource))
                {
                    throw new InvalidOperationException(
                        "At least one cache handle must be marked as the backplane source if a backplane is defined via configuration.");
                }

                CheckExtends<CacheBackplane>(configuration.BackplaneType);

                var args = new object[] { configuration, loggerFactory };
                if (configuration.BackplaneTypeArguments != null)
                {
                    args = configuration.BackplaneTypeArguments.Concat(args).ToArray();
                }

                return (CacheBackplane)CreateInstance(configuration.BackplaneType, args);
            }

            return null;
        }

        internal static ICollection<BaseCacheHandle<TCacheValue>> CreateCacheHandles<TCacheValue>(BaseCacheManager<TCacheValue> manager, ILoggerFactory loggerFactory, ICacheSerializer serializer)
        {
            NotNull(manager, nameof(manager));
            NotNull(loggerFactory, nameof(loggerFactory));

            var logger = loggerFactory.CreateLogger(nameof(CacheReflectionHelper));
            var managerConfiguration = manager.Configuration as ICacheManagerConfiguration;
            var handles = new List<BaseCacheHandle<TCacheValue>>();

            foreach (var handleConfiguration in managerConfiguration.CacheHandleConfigurations)
            {
                logger.LogInfo("Creating handle {0} of type {1}.", handleConfiguration.Name, handleConfiguration.HandleType);
                Type handleType = handleConfiguration.HandleType;
                Type instanceType = null;

                ValidateCacheHandleGenericTypeArguments(handleType);

                // if the configured type doesn't have a generic type definition ( <T> is not
                // defined )
#if NET40
                if (handleType.IsGenericTypeDefinition)
#else
                if (handleType.GetTypeInfo().IsGenericTypeDefinition)
#endif
                {
                    instanceType = handleType.MakeGenericType(new Type[] { typeof(TCacheValue) });
                }
                else
                {
                    instanceType = handleType;
                }

                var types = new List<object>(new object[] { loggerFactory, managerConfiguration, manager, handleConfiguration });
                if (serializer != null)
                {
                    types.Add(serializer);
                }

                var instance = CreateInstance(instanceType, types.ToArray()) as BaseCacheHandle<TCacheValue>;

                if (instance == null)
                {
                    throw new InvalidOperationException("Couldn't initialize handle of type " + instanceType.FullName);
                }

                handles.Add(instance);
            }

            if (handles.Count == 0)
            {
                throw new InvalidOperationException("No cache handles defined.");
            }

            return handles;
        }

        internal static object CreateInstance(Type instanceType, object[] knownInstances)
        {
#if NET40
            IEnumerable<ConstructorInfo> constructors = instanceType.GetConstructors();
#else
            IEnumerable<ConstructorInfo> constructors = instanceType.GetTypeInfo().DeclaredConstructors;
#endif

            constructors = constructors
                .Where(p => !p.IsStatic && p.IsPublic)
                .OrderByDescending(p => p.GetParameters().Length)
                .ToArray();

            if (constructors.Count() == 0)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "No matching public non static constructor found for type {0}.", instanceType.FullName));
            }

            object[] args = MatchArguments(constructors, knownInstances);

            try
            {
                return Activator.CreateInstance(instanceType, args);
            }
            catch (Exception ex)
            {
                if (ex.InnerException != null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Failed to initialize instance of type {0}.",
                            instanceType),
                        ex.InnerException);
                }

                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Failed to initialize instance of type {0}.",
                        instanceType),
                    ex);
            }
        }

        private static object[] MatchArguments(IEnumerable<ConstructorInfo> constructors, object[] instances)
        {
            ParameterInfo lastParamMiss = null;
            ConstructorInfo lastCtor = null;

            foreach (var constructor in constructors)
            {
                lastCtor = constructor;
                var args = new List<object>();
                var parameters = constructor.GetParameters();

                foreach (ParameterInfo param in parameters)
                {
#if NET40
                    var paramValue = instances
                        .Where(p => p != null)
                        .FirstOrDefault(p => param.ParameterType.IsAssignableFrom(p.GetType()));
#else
                    var paramValue = instances
                        .Where(p => p != null)
                        .FirstOrDefault(p => param.ParameterType.GetTypeInfo().IsAssignableFrom(p.GetType().GetTypeInfo()));
#endif
                    if (paramValue == null)
                    {
                        lastParamMiss = param;
                        break;
                    }

                    args.Add(paramValue);
                }

                if (parameters.Length == args.Count)
                {
                    return args.ToArray();
                }
            }

            if (constructors.Any(p => p.GetParameters().Length == 0))
            {
                // no match found, will try empty ctor
                return new object[] { };
            }

            // give more detailed error of what failed
            if (lastCtor != null && lastParamMiss != null)
            {
                var ctorTypes = string.Join(", ", lastCtor.GetParameters().Select(p => p.ParameterType.Name).ToArray());

                throw new InvalidOperationException(
                    $"Could not find a matching constructor for type '{lastCtor.DeclaringType.Name}'. Trying to match [{ctorTypes}] but missing {lastParamMiss.ParameterType.Name}");
            }

            throw new InvalidOperationException(
                $"Could not find a matching or empty constructor for type '{lastCtor.DeclaringType.Name}'.");
        }

        private static IEnumerable<Type> GetGenericBaseTypes(this Type type)
        {
#if NET40
            var baseType = type.BaseType;
            if (baseType == null || !baseType.IsGenericType)
#else
            var baseType = type.GetTypeInfo().BaseType;
            if (baseType == null || !baseType.GetTypeInfo().IsGenericType)
#endif
            {
                return Enumerable.Empty<Type>();
            }

#if NET40
            var genericBaseType = baseType.IsGenericTypeDefinition ? baseType : baseType.GetGenericTypeDefinition();
            return Enumerable.Repeat(genericBaseType, 1)
                .Concat(baseType.GetGenericBaseTypes());
#else
            var genericBaseType = baseType.GetTypeInfo().IsGenericTypeDefinition ? baseType : baseType.GetGenericTypeDefinition();
            return Enumerable.Repeat(genericBaseType, 1)
                .Concat(baseType.GetGenericBaseTypes());
#endif
        }

        private static void ValidateCacheHandleGenericTypeArguments(Type handle)
        {
            // not really needed due to the generic type from callees being restricted.
            if (!handle.GetGenericBaseTypes().Any(p => p == typeof(BaseCacheHandle<>)))
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Configured cache handle does not implement base cache handle [{0}].",
                        handle.ToString()));
            }

#if NETSTANDARD
            var handleInfo = handle.GetTypeInfo();
            if (handleInfo.IsGenericType && !handleInfo.IsGenericTypeDefinition)
#else
            if (handle.IsGenericType && !handle.IsGenericTypeDefinition)
#endif
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Cache handle type [{0}] should not have any generic arguments defined.",
                        handle.ToString()));
            }
        }

        private static void CheckImplements<TValid>(Type type)
        {
#if NETSTANDARD
            var typeInfo = type.GetTypeInfo();
            var interfaces = typeInfo.ImplementedInterfaces;
#else
            var interfaces = type.GetInterfaces();
#endif
            Ensure(
                interfaces.Any(p => p == typeof(TValid)),
                "Type must implement {0}, but {1} does not.",
                typeof(TValid).Name,
                type.FullName);
        }

        private static void CheckExtends<TValid>(Type type)
        {
#if NETSTANDARD
            var baseType = type.GetTypeInfo().BaseType;
#else
            var baseType = type.BaseType;
#endif

            while (baseType != typeof(object))
            {
                if (baseType == typeof(TValid))
                {
                    return;
                }
#if NETSTANDARD
                baseType = type.GetTypeInfo().BaseType;
#else
                baseType = type.BaseType;
#endif
            }

            throw new InvalidOperationException(
                string.Format(
                    "Type {0} does not extend from {1}.",
                    type.FullName,
                    typeof(TValid).Name));
        }
    }
}