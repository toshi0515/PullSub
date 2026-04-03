using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PullSub.Bridge;
using PullSub.Core;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace PullSub.Editor
{
    [InitializeOnLoad]
    internal static class PullSubTopicCatalogCodeGenerator
    {
        private sealed class TopicFieldInfo
        {
            public string Key;
            public Type PayloadType;
            public Type DeclaringType;
            public string FieldName;
            public string Source;
        }

        private const string OutputDirectory = "Assets/PullSub.Generated";
        private const string OutputFileName = "PullSubGeneratedTopicCatalogProvider.g.cs";
        private static bool _queued;

        static PullSubTopicCatalogCodeGenerator()
        {
            QueueGenerate();
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            QueueGenerate();
        }

        [MenuItem("Tools/PullSub/Generate Topic Catalog")]
        private static void GenerateFromMenu()
        {
            if (TryGenerate(out var changed))
            {
                PullSubTopicCatalog.Reload();
                if (changed)
                    AssetDatabase.Refresh();
            }
        }

        private static void QueueGenerate()
        {
            if (_queued)
                return;

            _queued = true;
            EditorApplication.delayCall += GenerateIfReady;
        }

        private static void GenerateIfReady()
        {
            _queued = false;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueueGenerate();
                return;
            }

            if (TryGenerate(out var changed))
            {
                PullSubTopicCatalog.Reload();
                if (changed)
                    AssetDatabase.Refresh();
            }
        }

        private static bool TryGenerate(out bool changed)
        {
            changed = false;

            var fields = CollectTopicFields(out var errors);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    Debug.LogError($"[PullSub] Topic catalog generation failed: {error}");

                return false;
            }

            var source = BuildSource(fields);
            var outputPath = Path.Combine(OutputDirectory, OutputFileName).Replace('\\', '/');

            if (!Directory.Exists(OutputDirectory))
                Directory.CreateDirectory(OutputDirectory);

            if (File.Exists(outputPath))
            {
                var existing = File.ReadAllText(outputPath, Encoding.UTF8);
                if (string.Equals(existing, source, StringComparison.Ordinal))
                    return true;
            }

            File.WriteAllText(outputPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Debug.Log($"[PullSub] Generated topic catalog: {outputPath}");
            changed = true;
            return true;
        }

        private static List<TopicFieldInfo> CollectTopicFields(out List<string> errors)
        {
            errors = new List<string>();
            var results = new List<TopicFieldInfo>();
            var keySet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsScanTargetAssembly(assembly))
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null)
                        continue;

                    var typeIgnored = type.GetCustomAttribute<PullSubTopicCatalogIgnoreAttribute>() != null;
                    if (typeIgnored)
                        continue;

                    var isConventionType = type.Name.EndsWith("Topics", StringComparison.Ordinal);
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    foreach (var field in fields)
                    {
                        if (field.GetCustomAttribute<PullSubTopicCatalogIgnoreAttribute>() != null)
                            continue;

                        var includeExplicit = field.GetCustomAttribute<PullSubTopicCatalogIncludeAttribute>() != null;
                        if (!isConventionType && !includeExplicit)
                            continue;

                        if (!field.IsPublic || !field.IsStatic || !field.IsInitOnly)
                        {
                            errors.Add($"{type.FullName}.{field.Name} must be public static readonly to be included in topic catalog.");
                            continue;
                        }

                        if (!TryGetPayloadType(field.FieldType, out var payloadType))
                            continue;

                        var alias = field.GetCustomAttribute<PullSubTopicKeyAliasAttribute>()?.Key;
                        var key = string.IsNullOrWhiteSpace(alias) ? field.Name : alias.Trim();
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            errors.Add($"{type.FullName}.{field.Name} resolved to an empty topic key.");
                            continue;
                        }

                        if (!keySet.Add(key))
                        {
                            errors.Add($"Duplicate topic key '{key}' detected. Source: {type.FullName}.{field.Name}");
                            continue;
                        }

                        results.Add(new TopicFieldInfo
                        {
                            Key = key,
                            PayloadType = payloadType,
                            DeclaringType = type,
                            FieldName = field.Name,
                            Source = $"{type.FullName}.{field.Name}"
                        });
                    }
                }
            }

            results.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return results;
        }

        private static bool IsScanTargetAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
                return false;

            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.StartsWith("Unity", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("mscorlib", StringComparison.Ordinal)
                || name.StartsWith("netstandard", StringComparison.Ordinal)
                || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mono.", StringComparison.Ordinal)
                || name.StartsWith("PullSub.", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetPayloadType(Type fieldType, out Type payloadType)
        {
            if (fieldType == null)
            {
                payloadType = null;
                return false;
            }

            if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(IPullSubTopic<>))
            {
                payloadType = fieldType.GetGenericArguments()[0];
                return true;
            }

            foreach (var iface in fieldType.GetInterfaces())
            {
                if (!iface.IsGenericType)
                    continue;

                if (iface.GetGenericTypeDefinition() != typeof(IPullSubTopic<>))
                    continue;

                payloadType = iface.GetGenericArguments()[0];
                return true;
            }

            payloadType = null;
            return false;
        }

        private static string BuildSource(List<TopicFieldInfo> fields)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine("using PullSub.Bridge;");
            sb.AppendLine();
            sb.AppendLine("namespace PullSub.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    [global::UnityEngine.Scripting.Preserve]");
            sb.AppendLine("    public sealed class PullSubGeneratedTopicCatalogProvider : IPullSubTopicCatalogProvider");
            sb.AppendLine("    {");
            sb.AppendLine("        public void Register(PullSubTopicCatalogBuilder builder)");
            sb.AppendLine("        {");

            foreach (var field in fields)
            {
                var payloadTypeName = GetTypeDisplayName(field.PayloadType);
                var declaringTypeName = GetTypeDisplayName(field.DeclaringType);
                sb.AppendLine(
                    $"            builder.Add<{payloadTypeName}>(PullSubTopicKey.From(\"{EscapeString(field.Key)}\"), {declaringTypeName}.{field.FieldName});");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public static class PullSubTopicKeys");
            sb.AppendLine("    {");

            var emittedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var memberName = ToSafeIdentifier(field.Key);
                var uniqueName = memberName;
                var suffix = 2;
                while (!emittedNames.Add(uniqueName))
                {
                    uniqueName = memberName + suffix;
                    suffix++;
                }

                sb.AppendLine(
                    $"        public static PullSubTopicKey {uniqueName} => PullSubTopicKey.From(\"{EscapeString(field.Key)}\");");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("#pragma warning restore");

            return sb.ToString();
        }

        private static string EscapeString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ToSafeIdentifier(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "TopicKey";

            var sb = new StringBuilder(key.Length + 8);
            if (!char.IsLetter(key[0]) && key[0] != '_')
                sb.Append('_');

            var makeUpper = true;
            foreach (var ch in key)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '_')
                {
                    makeUpper = true;
                    continue;
                }

                if (makeUpper)
                {
                    sb.Append(char.ToUpperInvariant(ch));
                    makeUpper = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }

            var result = sb.ToString();
            if (string.IsNullOrWhiteSpace(result))
                return "TopicKey";

            return result;
        }

        private static string GetTypeDisplayName(Type type)
        {
            if (type == null)
                return "global::System.Object";

            if (type.IsArray)
                return GetTypeDisplayName(type.GetElementType()) + "[]";

            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                var baseName = genericDef.FullName;
                var tickIndex = baseName.IndexOf('`');
                if (tickIndex >= 0)
                    baseName = baseName.Substring(0, tickIndex);

                var genericArgs = type.GetGenericArguments();
                var renderedArgs = string.Join(", ", genericArgs.Select(GetTypeDisplayName));
                return "global::" + baseName.Replace('+', '.') + "<" + renderedArgs + ">";
            }

            return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
        }
    }
}
