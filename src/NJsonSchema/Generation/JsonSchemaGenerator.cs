//-----------------------------------------------------------------------
// <copyright file="JsonSchemaGenerator.cs" company="NJsonSchema">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>https://github.com/rsuter/NJsonSchema/blob/master/LICENSE.md</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NJsonSchema.Annotations;
using NJsonSchema.Converters;
using NJsonSchema.Infrastructure;
using NJsonSchema.Generation.TypeMappers;

namespace NJsonSchema.Generation
{
    /// <summary>Generates a <see cref="JsonSchema4"/> object for a given type. </summary>
    public class JsonSchemaGenerator
    {
        private static readonly Dictionary<string, string> DataTypeFormats = new Dictionary<string, string>
        {
            {"DateTime", JsonFormatStrings.DateTime},
            {"Date", JsonFormatStrings.Date},
            {"Time", JsonFormatStrings.Time},
            {"EmailAddress", JsonFormatStrings.Email},
            {"PhoneNumber", JsonFormatStrings.Phone},
            {"Url", JsonFormatStrings.Uri}
        };

        /// <summary>Initializes a new instance of the <see cref="JsonSchemaGenerator"/> class.</summary>
        /// <param name="settings">The settings.</param>
        public JsonSchemaGenerator(JsonSchemaGeneratorSettings settings)
        {
            Settings = settings;
        }

        /// <summary>Gets the settings.</summary>
        public JsonSchemaGeneratorSettings Settings { get; }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<JsonSchema4> GenerateAsync(Type type)
        {
            var schema = new JsonSchema4();
            var schemaResolver = new JsonSchemaResolver(schema, Settings);
            await GenerateAsync(type, null, schema, schemaResolver).ConfigureAwait(false);
            return schema;
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public Task<JsonSchema4> GenerateAsync(Type type, JsonSchemaResolver schemaResolver)
        {
            return GenerateAsync<JsonSchema4>(type, schemaResolver);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public Task<TSchemaType> GenerateAsync<TSchemaType>(Type type, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            return GenerateAsync<TSchemaType>(type, null, schemaResolver);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<JsonSchema4> GenerateAsync(Type type, IEnumerable<Attribute> parentAttributes, JsonSchemaResolver schemaResolver)
        {
            return await GenerateAsync<JsonSchema4>(type, parentAttributes, schemaResolver).ConfigureAwait(false);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<TSchemaType> GenerateAsync<TSchemaType>(Type type, IEnumerable<Attribute> parentAttributes, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            var schema = new TSchemaType();
            await GenerateAsync(type, parentAttributes, schema, schemaResolver).ConfigureAwait(false);
            return schema;
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <typeparam name="TSchemaType">The type of the schema.</typeparam>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schema">The schema.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public virtual async Task GenerateAsync<TSchemaType>(Type type, IEnumerable<Attribute> parentAttributes, TSchemaType schema, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            ApplyExtensionDataAttributes(type, schema, parentAttributes);

            if (await TryHandleSpecialTypesAsync(type, schema, schemaResolver, parentAttributes).ConfigureAwait(false))
            {
                await ApplySchemaProcessorsAsync(type, schema, schemaResolver).ConfigureAwait(false);
                return;
            }

            if (schemaResolver.RootObject == schema)
                schema.Title = Settings.SchemaNameGenerator.Generate(type);

            var typeDescription = Settings.ReflectionService.GetDescription(type, parentAttributes, Settings);
            if (typeDescription.Type.HasFlag(JsonObjectType.Object))
            {
                if (typeDescription.IsDictionary)
                {
                    typeDescription.ApplyType(schema);
                    await GenerateDictionaryAsync(schema, type, schemaResolver).ConfigureAwait(false);
                }
                else
                {
                    if (schemaResolver.HasSchema(type, false))
                        schema.Reference = schemaResolver.GetSchema(type, false);
                    else if (schema.GetType() == typeof(JsonSchema4))
                    {
                        typeDescription.ApplyType(schema);
                        schema.Description = await type.GetTypeInfo().GetDescriptionAsync(type.GetTypeInfo().GetCustomAttributes()).ConfigureAwait(false);
                        await GenerateObjectAsync(type, schema, schemaResolver).ConfigureAwait(false);
                    }
                    else
                        schema.Reference = await GenerateAsync(type, parentAttributes, schemaResolver).ConfigureAwait(false);
                }
            }
            else if (typeDescription.IsEnum)
                await GenerateEnum(schema, type, parentAttributes, typeDescription, schemaResolver).ConfigureAwait(false);
            else if (typeDescription.Type.HasFlag(JsonObjectType.Array)) // TODO: Add support for tuples?
                await GenerateArray(schema, type, typeDescription, schemaResolver).ConfigureAwait(false);
            else
                typeDescription.ApplyType(schema);

            await ApplySchemaProcessorsAsync(type, schema, schemaResolver).ConfigureAwait(false);
        }

        /// <summary>Generetes a schema directly or referenced for the requested schema type; 
        /// does NOT change nullability.</summary>
        /// <typeparam name="TSchemaType">The resulted schema type which may reference the actual schema.</typeparam>
        /// <param name="type">The type of the schema to generate.</param>
        /// <param name="parentAttributes">The parent attributes (e.g. property or paramter attributes).</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <param name="transformation">An action to transform the resulting schema (e.g. property or parameter) before the type of reference is determined (with $ref or allOf/oneOf).</param>
        /// <returns>The requested schema object.</returns>
        public async Task<TSchemaType> GenerateWithReference<TSchemaType>(
            Type type,
            IEnumerable<Attribute> parentAttributes,
            JsonSchemaResolver schemaResolver,
            Func<TSchemaType, JsonSchema4, Task> transformation = null)
            where TSchemaType : JsonSchema4, new()
        {
            return await GenerateWithReferenceAndNullability<TSchemaType>(
                type, parentAttributes, false, schemaResolver, transformation).ConfigureAwait(false);
        }

        /// <summary>Generetes a schema directly or referenced for the requested schema type; 
        /// also adds nullability if required by looking at the type's <see cref="JsonTypeDescription" />.</summary>
        /// <typeparam name="TSchemaType">The resulted schema type which may reference the actual schema.</typeparam>
        /// <param name="type">The type of the schema to generate.</param>
        /// <param name="parentAttributes">The parent attributes (e.g. property or paramter attributes).</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <param name="transformation">An action to transform the resulting schema (e.g. property or parameter) before the type of reference is determined (with $ref or allOf/oneOf).</param>
        /// <returns>The requested schema object.</returns>
        public async Task<TSchemaType> GenerateWithReferenceAndNullability<TSchemaType>(
            Type type, IEnumerable<Attribute> parentAttributes, JsonSchemaResolver schemaResolver,
            Func<TSchemaType, JsonSchema4, Task> transformation = null)
            where TSchemaType : JsonSchema4, new()
        {
            var typeDescription = Settings.ReflectionService.GetDescription(type, parentAttributes, Settings);

            return await GenerateWithReferenceAndNullability(
                type, parentAttributes, typeDescription.IsNullable, schemaResolver, transformation).ConfigureAwait(false);
        }

        /// <summary>Generetes a schema directly or referenced for the requested schema type; also adds nullability if required.</summary>
        /// <typeparam name="TSchemaType">The resulted schema type which may reference the actual schema.</typeparam>
        /// <param name="type">The type of the schema to generate.</param>
        /// <param name="parentAttributes">The parent attributes (e.g. property or paramter attributes).</param>
        /// <param name="isNullable">Specifies whether the property, parameter or requested schema type is nullable.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <param name="transformation">An action to transform the resulting schema (e.g. property or parameter) before the type of reference is determined (with $ref or allOf/oneOf).</param>
        /// <returns>The requested schema object.</returns>
        public virtual async Task<TSchemaType> GenerateWithReferenceAndNullability<TSchemaType>(
            Type type, IEnumerable<Attribute> parentAttributes, bool isNullable, JsonSchemaResolver schemaResolver,
            Func<TSchemaType, JsonSchema4, Task> transformation = null)
            where TSchemaType : JsonSchema4, new()
        {
            var typeDescription = Settings.ReflectionService.GetDescription(type, parentAttributes, Settings);
            var requiresSchemaReference = typeDescription.RequiresSchemaReference(Settings.TypeMappers);

            JsonSchema4 referencedSchema;
            if (!requiresSchemaReference)
            {
                var schema = await GenerateAsync<TSchemaType>(type, parentAttributes, schemaResolver).ConfigureAwait(false);
                if (!schema.HasReference)
                {
                    if (transformation != null)
                        await transformation(schema, schema).ConfigureAwait(false);

                    if (isNullable)
                    {
                        if (Settings.SchemaType != SchemaType.Swagger2)
                        {
                            if (schema.Type == JsonObjectType.None)
                            {
                                schema.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.None });
                                schema.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.Null });
                            }
                            else
                                schema.Type = schema.Type | JsonObjectType.Null;
                        }
                    }

                    return schema;
                }
                else
                    referencedSchema = schema.ActualSchema;
            }
            else
                referencedSchema = await GenerateAsync<JsonSchema4>(type, parentAttributes, schemaResolver).ConfigureAwait(false);

            var referencingSchema = new TSchemaType();
            if (transformation != null)
                await transformation(referencingSchema, referencedSchema).ConfigureAwait(false);

            if (isNullable && Settings.SchemaType != SchemaType.Swagger2)
                referencingSchema.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.Null });

            // See https://github.com/RSuter/NJsonSchema/issues/531
            var useDirectReference = Settings.AllowReferencesWithProperties ||
                !JsonConvert.DeserializeObject<JObject>(JsonConvert.SerializeObject(referencingSchema)).Properties().Any(); // TODO: Improve performance

            if (useDirectReference && referencingSchema.OneOf.Count == 0)
            {
                referencingSchema.Reference = referencedSchema.ActualSchema;
            }
            else if (Settings.SchemaType != SchemaType.Swagger2)
            {
                referencingSchema.OneOf.Add(new JsonSchema4
                {
                    Reference = referencedSchema.ActualSchema
                });
            }
            else
            {
                referencingSchema.AllOf.Add(new JsonSchema4
                {
                    Reference = referencedSchema.ActualSchema
                });
            }

            return referencingSchema;
        }

        /// <summary>Gets the converted property name.</summary>
        /// <param name="property">The property.</param>
        /// <param name="memberInfo">The member info.</param>
        /// <returns>The property name.</returns>
        public virtual string GetPropertyName(Newtonsoft.Json.Serialization.JsonProperty property, MemberInfo memberInfo)
        {
            try
            {
                var propertyName = memberInfo != null ? ReflectionCache.GetPropertiesAndFields(memberInfo.DeclaringType)
                    .First(p => p.MemberInfo.Name == memberInfo.Name).GetName() : property.PropertyName;

                var contractResolver = Settings.ActualContractResolver as DefaultContractResolver;
                return contractResolver != null
                    ? contractResolver.GetResolvedPropertyName(propertyName)
                    : propertyName;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Could not get JSON property name of property '" +
                    (memberInfo != null ? memberInfo.Name : "n/a") + "' and type '" +
                    (memberInfo?.DeclaringType != null ? memberInfo.DeclaringType.FullName : "n/a") + "'.", e);
            }
        }

        /// <summary>Generates the properties for the given type and schema.</summary>
        /// <typeparam name="TSchemaType">The type of the schema type.</typeparam>
        /// <param name="type">The types.</param>
        /// <param name="schema">The properties</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The task.</returns>
        protected virtual async Task GenerateObjectAsync<TSchemaType>(
            Type type, TSchemaType schema, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            schemaResolver.AddSchema(type, false, schema);

            schema.AllowAdditionalProperties = false;
            schema.IsAbstract = type.GetTypeInfo().IsAbstract;

            await GeneratePropertiesAndInheritanceAsync(type, schema, schemaResolver).ConfigureAwait(false);

            if (Settings.GenerateKnownTypes)
                await GenerateKnownTypesAsync(type, schemaResolver).ConfigureAwait(false);

            if (Settings.GenerateXmlObjects)
                schema.GenerateXmlObjectForType(type);
        }

        private async Task ApplySchemaProcessorsAsync(Type type, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
            var context = new SchemaProcessorContext(type, schema, schemaResolver, this);
            foreach (var processor in Settings.SchemaProcessors)
                await processor.ProcessAsync(context).ConfigureAwait(false);
        }

        private void ApplyExtensionDataAttributes<TSchemaType>(Type type, TSchemaType schema, IEnumerable<Attribute> parentAttributes)
            where TSchemaType : JsonSchema4, new()
        {
            if (parentAttributes == null)
            {
                // class
                var extensionDataAttributes = type.GetTypeInfo().GetCustomAttributes<JsonSchemaExtensionDataAttribute>().ToArray();
                if (extensionDataAttributes.Any())
                    schema.ExtensionData = extensionDataAttributes.ToDictionary(a => a.Property, a => a.Value);
            }
            else
            {
                // property or parameter
                var extensionDataAttributes = parentAttributes.OfType<JsonSchemaExtensionDataAttribute>().ToArray();
                if (extensionDataAttributes.Any())
                    schema.ExtensionData = extensionDataAttributes.ToDictionary(a => a.Property, a => a.Value);
            }
        }

        private async Task<bool> TryHandleSpecialTypesAsync<TSchemaType>(Type type, TSchemaType schema,
            JsonSchemaResolver schemaResolver, IEnumerable<Attribute> parentAttributes)
            where TSchemaType : JsonSchema4, new()
        {
            var typeMapper = Settings.TypeMappers.FirstOrDefault(m => m.MappedType == type);
            if (typeMapper == null && type.GetTypeInfo().IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();
                typeMapper = Settings.TypeMappers.FirstOrDefault(m => m.MappedType == genericType);
            }

            if (typeMapper != null)
            {
                var context = new TypeMapperContext(type, this, schemaResolver, parentAttributes);
                await typeMapper.GenerateSchemaAsync(schema, context).ConfigureAwait(false);
                return true;
            }

            if (type == typeof(JObject) || type == typeof(JToken) || type == typeof(object))
                return true;

            return false;
        }

        private async Task GenerateArray<TSchemaType>(
            TSchemaType schema, Type type, JsonTypeDescription typeDescription, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            typeDescription.ApplyType(schema);

            var itemType = type.GetEnumerableItemType();
            if (itemType != null)
            {
                schema.Item = await GenerateWithReferenceAndNullability<JsonSchema4>(
#pragma warning disable 1998
                    itemType, null, false, schemaResolver, async (s, r) =>
#pragma warning restore 1998
                    {
                        if (Settings.GenerateXmlObjects)
                            s.GenerateXmlObjectForItemType(itemType);
                    }).ConfigureAwait(false);
            }
            else
                schema.Item = JsonSchema4.CreateAnySchema();
        }

        private async Task GenerateEnum<TSchemaType>(
            TSchemaType schema, Type type, IEnumerable<Attribute> parentAttributes, JsonTypeDescription typeDescription, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            if (type.Name == "Nullable`1")
#if !LEGACY
                type = type.GenericTypeArguments[0];
#else
                type = type.GetGenericArguments()[0];
#endif

            var isIntegerEnumeration = typeDescription.Type == JsonObjectType.Integer;
            if (schemaResolver.HasSchema(type, isIntegerEnumeration))
                schema.Reference = schemaResolver.GetSchema(type, isIntegerEnumeration);
            else if (schema.GetType() == typeof(JsonSchema4))
            {
                LoadEnumerations(type, schema, typeDescription);

                typeDescription.ApplyType(schema);
                schema.Description = await type.GetXmlSummaryAsync().ConfigureAwait(false);

                schemaResolver.AddSchema(type, isIntegerEnumeration, schema);
            }
            else
                schema.Reference = await GenerateAsync(type, parentAttributes, schemaResolver).ConfigureAwait(false);
        }

        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        private async Task GenerateDictionaryAsync<TSchemaType>(TSchemaType schema, Type type, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            var genericTypeArguments = type.GetGenericTypeArguments();

            var valueType = genericTypeArguments.Length == 2 ? genericTypeArguments[1] : typeof(object);
            if (valueType == typeof(object))
                schema.AdditionalPropertiesSchema = JsonSchema4.CreateAnySchema();
            else
            {
                var additionalPropertiesSchema = await GenerateAsync(valueType, schemaResolver).ConfigureAwait(false);
                var valueTypeDescription = Settings.ReflectionService.GetDescription(valueType, null, Settings);
                if (valueTypeDescription.RequiresSchemaReference(Settings.TypeMappers))
                {
                    schema.AdditionalPropertiesSchema = new JsonSchema4
                    {
                        Reference = additionalPropertiesSchema
                    };
                }
                else
                    schema.AdditionalPropertiesSchema = additionalPropertiesSchema;
            }

            schema.AllowAdditionalProperties = true;
        }

        private async Task GeneratePropertiesAndInheritanceAsync(Type type, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
#if !LEGACY
            var propertiesAndFields = type.GetTypeInfo()
                .DeclaredFields
                .Where(f => f.IsPublic && !f.IsStatic)
                .OfType<MemberInfo>()
                .Concat(
                    type.GetTypeInfo().DeclaredProperties
                    .Where(p => (p.GetMethod?.IsPublic == true && p.GetMethod?.IsStatic == false) ||
                                (p.SetMethod?.IsPublic == true && p.SetMethod?.IsStatic == false))
                )
                .ToList();
#else
            var propertiesAndFields = type.GetTypeInfo()
                .GetFields(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.IsPublic && !f.IsStatic)
                .OfType<MemberInfo>()
                .Concat(
                    type.GetTypeInfo()
                    .GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => (p.GetGetMethod()?.IsPublic == true && p.GetGetMethod()?.IsStatic == false) ||
                                (p.GetSetMethod()?.IsPublic == true && p.GetSetMethod()?.IsStatic == false))
                )
                .ToList();
#endif

            var contract = Settings.ResolveContract(type);

            var allowedProperties = GetTypeProperties(type);
            var objectContract = contract as JsonObjectContract;
            if (objectContract != null && allowedProperties == null)
            {
                foreach (var property in objectContract.Properties.Where(p => p.DeclaringType == type))
                {
                    bool shouldSerialize;
                    try
                    {
                        shouldSerialize = property.ShouldSerialize?.Invoke(null) != false;
                    }
                    catch
                    {
                        shouldSerialize = true;
                    }

                    if (shouldSerialize)
                    {
                        var info = propertiesAndFields.FirstOrDefault(p => p.Name == property.UnderlyingName);
                        var propertyInfo = info as PropertyInfo;
#if !LEGACY
                        if (Settings.GenerateAbstractProperties || propertyInfo == null ||
                            (propertyInfo.GetMethod?.IsAbstract != true && propertyInfo.SetMethod?.IsAbstract != true))
#else
                        if (Settings.GenerateAbstractProperties || propertyInfo == null ||
                            (propertyInfo.GetGetMethod()?.IsAbstract != true && propertyInfo.GetSetMethod()?.IsAbstract != true))
#endif
                        {
                            await LoadPropertyOrFieldAsync(property, info, type, schema, schemaResolver).ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                // TODO: Remove this hacky code (used to support serialization of exceptions and restore the old behavior [pre 9.x])
                foreach (var info in propertiesAndFields.Where(m => allowedProperties == null || allowedProperties.Contains(m.Name)))
                {
                    var attribute = info.GetCustomAttributes(true).OfType<JsonPropertyAttribute>().SingleOrDefault();
                    var propertyType = (info as PropertyInfo)?.PropertyType ?? ((FieldInfo)info).FieldType;
                    var property = new Newtonsoft.Json.Serialization.JsonProperty
                    {
                        AttributeProvider = new ReflectionAttributeProvider(info),
                        PropertyType = propertyType,
                        Ignored = IsPropertyIgnored(propertyType, type, info.GetCustomAttributes(true).OfType<Attribute>().ToArray())
                    };

                    if (attribute != null)
                    {
                        property.PropertyName = attribute.PropertyName ?? info.Name;
                        property.Required = attribute.Required;
                        property.DefaultValueHandling = attribute.DefaultValueHandling;
                        property.TypeNameHandling = attribute.TypeNameHandling;
                        property.NullValueHandling = attribute.NullValueHandling;
                        property.TypeNameHandling = attribute.TypeNameHandling;
                    }
                    else
                    {
                        property.PropertyName = info.Name;
                    }

                    await LoadPropertyOrFieldAsync(property, info, type, schema, schemaResolver).ConfigureAwait(false);
                }
            }

            await GenerateInheritanceAsync(type, schema, schemaResolver).ConfigureAwait(false);
        }

        /// <summary>Gets the properties of the given type or null to take all properties.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The property names or null for all.</returns>
        protected virtual string[] GetTypeProperties(Type type)
        {
            if (type == typeof(Exception))
                return new[] { "InnerException", "Message", "Source", "StackTrace" };

            return null;
        }

        private async Task GenerateKnownTypesAsync(Type type, JsonSchemaResolver schemaResolver)
        {
            var knownTypeAttributes = type.GetTypeInfo()
                .GetCustomAttributes(Settings.FlattenInheritanceHierarchy) // Known types of inherited classes will be generated later (in GenerateInheritanceAsync)
                .Where(a => a.GetType().Name == "KnownTypeAttribute")
                .OfType<Attribute>();

            foreach (dynamic attribute in knownTypeAttributes)
            {
                if (attribute.Type != null)
                    await AddKnownTypeAsync(attribute.Type, schemaResolver).ConfigureAwait(false);
                else if (attribute.MethodName != null)
                {
                    var methodInfo = type.GetRuntimeMethod((string)attribute.MethodName, new Type[0]);
                    if (methodInfo != null)
                    {
                        var knownTypes = methodInfo.Invoke(null, null) as IEnumerable<Type>;
                        if (knownTypes != null)
                        {
                            foreach (var knownType in knownTypes)
                                await AddKnownTypeAsync(knownType, schemaResolver).ConfigureAwait(false);
                        }
                    }
                }
                else
                    throw new ArgumentException($"A KnownType attribute on {type.FullName} does not specify a type or a method name.", nameof(type));
            }
        }

        private async Task AddKnownTypeAsync(Type type, JsonSchemaResolver schemaResolver)
        {
            var typeDescription = Settings.ReflectionService.GetDescription(type, null, Settings);
            var isIntegerEnum = typeDescription.Type == JsonObjectType.Integer;

            if (!schemaResolver.HasSchema(type, isIntegerEnum))
                await GenerateAsync(type, schemaResolver).ConfigureAwait(false);
        }

        private async Task GenerateInheritanceAsync(Type type, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
            GenerateInheritanceDiscriminator(type, schema);

            var baseType = type.GetTypeInfo().BaseType;
            if (baseType != null && baseType != typeof(object))
            {
                if (baseType.GetTypeInfo().GetCustomAttributes(false).TryGetIfAssignableTo("JsonSchemaIgnoreAttribute", TypeNameStyle.Name) == null &&
                    baseType.GetTypeInfo().GetCustomAttributes(false).TryGetIfAssignableTo("SwaggerIgnoreAttribute", TypeNameStyle.Name) == null &&
                    Settings.ExcludedTypeNames?.Contains(baseType.FullName) != true)
                {
                    if (Settings.FlattenInheritanceHierarchy)
                    {
                        var typeDescription = Settings.ReflectionService.GetDescription(baseType, null, Settings);
                        if (!typeDescription.IsDictionary && !type.IsArray)
                            await GeneratePropertiesAndInheritanceAsync(baseType, schema, schemaResolver).ConfigureAwait(false);
                    }
                    else
                    {
                        var baseSchema = await GenerateAsync(baseType, schemaResolver).ConfigureAwait(false);
                        var baseTypeInfo = Settings.ReflectionService.GetDescription(baseType, null, Settings);
                        if (baseTypeInfo.RequiresSchemaReference(Settings.TypeMappers))
                        {
                            if (schemaResolver.RootObject != baseSchema.ActualSchema)
                                schemaResolver.AppendSchema(baseSchema.ActualSchema, Settings.SchemaNameGenerator.Generate(baseType));

                            schema.AllOf.Add(new JsonSchema4
                            {
                                Reference = baseSchema.ActualSchema
                            });
                        }
                        else
                            schema.AllOf.Add(baseSchema);
                    }
                }
            }

            if (Settings.FlattenInheritanceHierarchy && Settings.GenerateAbstractProperties)
            {
#if !LEGACY
                foreach (var i in type.GetTypeInfo().ImplementedInterfaces)
#else
                foreach (var i in type.GetTypeInfo().GetInterfaces())
#endif
                {
                    var typeDescription = Settings.ReflectionService.GetDescription(i, null, Settings);
                    if (!typeDescription.IsDictionary && !type.IsArray && !typeof(IEnumerable).GetTypeInfo().IsAssignableFrom(i.GetTypeInfo()))
                        await GeneratePropertiesAndInheritanceAsync(i, schema, schemaResolver).ConfigureAwait(false);
                }
            }
        }

        private void GenerateInheritanceDiscriminator(Type type, JsonSchema4 schema)
        {
            if (!Settings.FlattenInheritanceHierarchy)
            {
                var discriminator = TryGetInheritanceDiscriminator(type);
                if (!string.IsNullOrEmpty(discriminator))
                {
                    if (schema.Properties.ContainsKey(discriminator))
                        throw new InvalidOperationException("The JSON property '" + discriminator + "' is defined multiple times on type '" + type.FullName + "'.");

                    schema.Discriminator = discriminator;
                    schema.Properties[discriminator] = new JsonProperty
                    {
                        Type = JsonObjectType.String,
                        IsRequired = true
                    };
                }
            }
        }

        private string TryGetInheritanceDiscriminator(Type type)
        {
            var typeAttributes = type.GetTypeInfo().GetCustomAttributes(false).OfType<Attribute>();

            dynamic jsonConverterAttribute = typeAttributes.TryGetIfAssignableTo("JsonConverterAttribute", TypeNameStyle.Name);
            if (jsonConverterAttribute != null)
            {
                var converterType = (Type)jsonConverterAttribute.ConverterType;
                if (converterType.Name == "JsonInheritanceConverter")
                {
                    if (jsonConverterAttribute.ConverterParameters != null && jsonConverterAttribute.ConverterParameters.Length > 0)
                        return jsonConverterAttribute.ConverterParameters[0];
                    return JsonInheritanceConverter.DefaultDiscriminatorName;
                }
            }
            return null;
        }

        private void LoadEnumerations(Type type, JsonSchema4 schema, JsonTypeDescription typeDescription)
        {
            schema.Type = typeDescription.Type;
            schema.Enumeration.Clear();
            schema.EnumerationNames.Clear();

            var underlyingType = Enum.GetUnderlyingType(type);

            foreach (var enumName in Enum.GetNames(type))
            {
                if (typeDescription.Type == JsonObjectType.Integer)
                {
                    var value = Convert.ChangeType(Enum.Parse(type, enumName), underlyingType);
                    schema.Enumeration.Add(value);
                }
                else
                {
                    var attributes = type.GetTypeInfo().GetDeclaredField(enumName).GetCustomAttributes(); // EnumMember only checked if StringEnumConverter is used
                    dynamic enumMemberAttribute = attributes.TryGetIfAssignableTo("System.Runtime.Serialization.EnumMemberAttribute");
                    if (enumMemberAttribute != null && !string.IsNullOrEmpty(enumMemberAttribute.Value))
                        schema.Enumeration.Add((string)enumMemberAttribute.Value);
                    else
                        schema.Enumeration.Add(enumName);
                }

                schema.EnumerationNames.Add(enumName);
            }
        }

        private async Task LoadPropertyOrFieldAsync(Newtonsoft.Json.Serialization.JsonProperty property, MemberInfo propertyInfo, Type parentType, JsonSchema4 parentSchema, JsonSchemaResolver schemaResolver)
        {
            var propertyType = property.PropertyType;
            var propertyAttributes = property.AttributeProvider.GetAttributes(true).ToArray();
            var propertyTypeDescription = Settings.ReflectionService.GetDescription(propertyType, propertyAttributes, Settings);
            if (property.Ignored == false && IsPropertyIgnoredBySettings(propertyType, parentType, propertyAttributes) == false)
            {
                if (propertyType.Name == "Nullable`1")
#if !LEGACY
                    propertyType = propertyType.GenericTypeArguments[0];
#else
                    propertyType = propertyType.GetGenericArguments()[0];
#endif

                var propertyName = GetPropertyName(property, propertyInfo);
                if (parentSchema.Properties.ContainsKey(propertyName))
                    throw new InvalidOperationException("The JSON property '" + propertyName + "' is defined multiple times on type '" + parentType.FullName + "'.");

                var requiredAttribute = propertyAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RequiredAttribute");

                var hasJsonNetAttributeRequired = property.Required == Required.Always || property.Required == Required.AllowNull;
                var isDataContractMemberRequired = GetDataMemberAttribute(parentType, propertyAttributes)?.IsRequired == true;

                var hasRequiredAttribute = requiredAttribute != null;
                if (hasRequiredAttribute || isDataContractMemberRequired || hasJsonNetAttributeRequired)
                    parentSchema.RequiredProperties.Add(propertyName);

                var isNullable = propertyTypeDescription.IsNullable &&
                    hasRequiredAttribute == false &&
                    (bool)isDataContractMemberRequired == false &&
                    (property.Required == Required.Default || property.Required == Required.AllowNull);

                var jsonProperty = await GenerateWithReferenceAndNullability<JsonProperty>(
                    propertyType, propertyAttributes, isNullable, schemaResolver, async (p, s) =>
                    {
                        if (Settings.GenerateXmlObjects)
                            p.GenerateXmlObjectForProperty(parentType, propertyName, propertyAttributes);

                        if (Settings.SchemaType == SchemaType.JsonSchema &&
                            hasRequiredAttribute &&
                            requiredAttribute.TryGetPropertyValue("AllowEmptyStrings", false) == false)
                        {
                            p.MinLength = 1;
                        }

                        if (!isNullable && Settings.SchemaType == SchemaType.Swagger2)
                        {
                            if (!parentSchema.RequiredProperties.Contains(propertyName))
                                parentSchema.RequiredProperties.Add(propertyName);
                        }

                        dynamic readOnlyAttribute = propertyAttributes.TryGetIfAssignableTo("System.ComponentModel.ReadOnlyAttribute");
                        if (readOnlyAttribute != null)
                            p.IsReadOnly = readOnlyAttribute.IsReadOnly;

                        if (p.Description == null)
                            p.Description = await propertyInfo.GetDescriptionAsync(propertyAttributes).ConfigureAwait(false);

                        p.Default = ConvertDefaultValue(property);

                        ApplyDataAnnotations(p, propertyTypeDescription, propertyAttributes);
                    }).ConfigureAwait(false);

                parentSchema.Properties.Add(propertyName, jsonProperty);
            }
        }

        private bool IsPropertyIgnored(Type propertyType, Type parentType, Attribute[] propertyAttributes)
        {
            if (propertyAttributes.Any(a => a is JsonIgnoreAttribute))
                return true;

            if (HasDataContractAttribute(parentType) && GetDataMemberAttribute(parentType, propertyAttributes) == null && !propertyAttributes.Any(a => a is JsonPropertyAttribute))
                return true;

            return IsPropertyIgnoredBySettings(propertyType, parentType, propertyAttributes);
        }

        private bool IsPropertyIgnoredBySettings(Type propertyType, Type parentType, Attribute[] propertyAttributes)
        {
            if (Settings.IgnoreObsoleteProperties && propertyAttributes.Any(a => a is ObsoleteAttribute))
                return true;

            return false;
        }

        private static dynamic GetDataMemberAttribute(Type parentType, Attribute[] propertyAttributes)
        {
            if (!HasDataContractAttribute(parentType))
                return null;

            return propertyAttributes.TryGetIfAssignableTo("DataMemberAttribute", TypeNameStyle.Name);
        }

        private static bool HasDataContractAttribute(Type parentType)
        {
            return parentType.GetTypeInfo().GetCustomAttributes()
                .TryGetIfAssignableTo("DataContractAttribute", TypeNameStyle.Name) != null;
        }

        /// <summary>Applies the property annotations to the JSON property.</summary>
        /// <param name="schema">The schema.</param>
        /// <param name="typeDescription">The property type description.</param>
        /// <param name="parentAttributes">The attributes.</param>
        public virtual void ApplyDataAnnotations(JsonSchema4 schema, JsonTypeDescription typeDescription, IEnumerable<Attribute> parentAttributes)
        {
            // TODO: Refactor out

            dynamic displayAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.DisplayAttribute");
            if (displayAttribute != null && displayAttribute.Name != null)
                schema.Title = displayAttribute.Name;

            dynamic defaultValueAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DefaultValueAttribute");
            if (defaultValueAttribute != null)
                schema.Default = defaultValueAttribute.Value;

            dynamic regexAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RegularExpressionAttribute");
            if (regexAttribute != null)
            {
                if (typeDescription.IsDictionary)
                    schema.AdditionalPropertiesSchema.Pattern = regexAttribute.Pattern;
                else
                    schema.Pattern = regexAttribute.Pattern;
            }

            if (typeDescription.Type == JsonObjectType.Number ||
                typeDescription.Type == JsonObjectType.Integer)
            {
                dynamic rangeAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RangeAttribute");
                if (rangeAttribute != null)
                {
                    if (rangeAttribute.Minimum != null && rangeAttribute.Minimum > double.MinValue)
                        schema.Minimum = (decimal?)(double)rangeAttribute.Minimum;
                    if (rangeAttribute.Maximum != null && rangeAttribute.Maximum < double.MaxValue)
                        schema.Maximum = (decimal?)(double)rangeAttribute.Maximum;
                }

                var multipleOfAttribute = parentAttributes.OfType<MultipleOfAttribute>().SingleOrDefault();
                if (multipleOfAttribute != null)
                    schema.MultipleOf = multipleOfAttribute.MultipleOf;
            }

            dynamic minLengthAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.MinLengthAttribute");
            if (minLengthAttribute != null && minLengthAttribute.Length != null)
            {
                if (typeDescription.Type == JsonObjectType.String)
                    schema.MinLength = minLengthAttribute.Length;
                else if (typeDescription.Type == JsonObjectType.Array)
                    schema.MinItems = minLengthAttribute.Length;
            }

            dynamic maxLengthAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.MaxLengthAttribute");
            if (maxLengthAttribute != null && maxLengthAttribute.Length != null)
            {
                if (typeDescription.Type == JsonObjectType.String)
                    schema.MaxLength = maxLengthAttribute.Length;
                else if (typeDescription.Type == JsonObjectType.Array)
                    schema.MaxItems = maxLengthAttribute.Length;
            }

            dynamic stringLengthAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.StringLengthAttribute");
            if (stringLengthAttribute != null)
            {
                if (typeDescription.Type == JsonObjectType.String)
                {
                    schema.MinLength = stringLengthAttribute.MinimumLength;
                    schema.MaxLength = stringLengthAttribute.MaximumLength;
                }
            }

            dynamic dataTypeAttribute = parentAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.DataTypeAttribute");
            if (dataTypeAttribute != null)
            {
                var dataType = dataTypeAttribute.DataType.ToString();
                if (DataTypeFormats.ContainsKey(dataType))
                    schema.Format = DataTypeFormats[dataType];
            }
        }

        private object ConvertDefaultValue(Newtonsoft.Json.Serialization.JsonProperty property)
        {
            if (property.DefaultValue != null && property.DefaultValue.GetType().GetTypeInfo().IsEnum)
            {
                var hasStringEnumConverter = typeof(StringEnumConverter).GetTypeInfo().IsAssignableFrom(property.Converter?.GetType().GetTypeInfo());
                if (hasStringEnumConverter)
                    return property.DefaultValue.ToString();
                else
                    return (int)property.DefaultValue;
            }
            else
                return property.DefaultValue;
        }
    }
}
