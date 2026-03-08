using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Events;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    /// <summary>
    /// Commands for manipulating component fields and properties.
    /// </summary>
    public static class ComponentCommands
    {
        /// <summary>
        /// Set a field or property on a component by finding the target GameObject and component.
        /// JSON: {"gameObject":"Path/To/Object", "component":"ComponentName", "field":"fieldName", "value":"Path/To/Target" or value}
        /// </summary>
        [BridgeCommand("COMPONENT_SET", "Set a field or property value on a component",
            Category = "Component",
            Usage = "COMPONENT_SET Canvas/Panel Image m_Color #FF0000\n" +
                    "  COMPONENT_SET {\"gameObject\":\"Canvas/Panel\",\"component\":\"Image\",\"field\":\"m_Color\",\"value\":\"#FF0000\"}",
            RequiresMainThread = true)]
        public static string Set(string jsonData)
        {
            try
            {
                string gameObjectPath;
                string componentName;
                string fieldName;
                JToken valueToken;

                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                    fieldName = data["field"]?.ToString();
                    valueToken = data["value"];
                }
                else
                {
                    // Plain args: COMPONENT_SET <gameObject> <component> <field> <value...>
                    var parts = jsonData.Trim().Split(new[] { ' ' }, 4);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                    fieldName = parts.Length > 2 ? parts[2] : null;
                    valueToken = parts.Length > 3 ? JToken.FromObject(parts[3]) : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");
                if (string.IsNullOrEmpty(fieldName))
                    return Response.Error("field name is required");

                // Find the GameObject
                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return Response.Error($"GameObject not found: {gameObjectPath}");

                // Find the component
                var component = go.GetComponents<Component>()
                    .FirstOrDefault(c => c.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                    return Response.Error($"Component '{componentName}' not found on {gameObjectPath}");

                // Find the field or property
                var componentType = component.GetType();
                var field = componentType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var property = componentType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field == null && property == null)
                    return Response.Error($"Field or property '{fieldName}' not found on component {componentName}");

                // Determine the target type
                Type targetType = field?.FieldType ?? property?.PropertyType;

                // Parse the value
                object value = ParseValue(valueToken, targetType);

                // Set the value
                Undo.RecordObject(component, $"Set {fieldName}");
                if (field != null)
                    field.SetValue(component, value);
                else
                    property.SetValue(component, value);

                EditorUtility.SetDirty(component);

                return Response.SuccessWithData(new
                {
                    gameObject = gameObjectPath,
                    component = componentName,
                    field = fieldName,
                    value = value?.ToString() ?? "null"
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Add a component to a GameObject.
        /// </summary>
        [BridgeCommand("ADDCOMPONENT", "Add a component to a GameObject",
            Category = "Component",
            Usage = "ADDCOMPONENT Canvas/Panel BoxCollider\n" +
                    "  ADDCOMPONENT {\"gameObject\":\"Canvas/Panel\",\"component\":\"BoxCollider\"}",
            RequiresMainThread = true)]
        public static string AddComponent(string jsonData)
        {
            try
            {
                string gameObjectPath, componentName;
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                }
                else
                {
                    // Plain args: ADDCOMPONENT <gameObject> <component>
                    var parts = jsonData.Trim().Split(new[] { ' ' }, 2);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");

                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return Response.Error($"GameObject not found: {gameObjectPath}");

                var componentType = FindType(componentName);
                if (componentType == null)
                    return Response.Error($"Component type not found: {componentName}");

                if (!typeof(Component).IsAssignableFrom(componentType))
                    return Response.Error($"Type '{componentName}' is not a Component");

                Undo.RecordObject(go, $"Add {componentName}");
                var added = go.AddComponent(componentType);
                EditorUtility.SetDirty(go);

                return Response.Success($"Added {added.GetType().Name} to {gameObjectPath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Remove a component from a GameObject.
        /// </summary>
        [BridgeCommand("REMOVECOMPONENT", "Remove a component from a GameObject",
            Category = "Component",
            Usage = "REMOVECOMPONENT Canvas/Panel BoxCollider\n" +
                    "  REMOVECOMPONENT {\"gameObject\":\"Canvas/Panel\",\"component\":\"BoxCollider\"}",
            RequiresMainThread = true)]
        public static string RemoveComponent(string jsonData)
        {
            try
            {
                string gameObjectPath, componentName;
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                }
                else
                {
                    var parts = jsonData.Trim().Split(new[] { ' ' }, 2);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");

                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return Response.Error($"GameObject not found: {gameObjectPath}");

                var component = go.GetComponents<Component>()
                    .FirstOrDefault(c => c.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                    return Response.Error($"Component '{componentName}' not found on {gameObjectPath}");

                Undo.DestroyObjectImmediate(component);
                EditorUtility.SetDirty(go);

                return Response.Success($"Removed {componentName} from {gameObjectPath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Inspect a GameObject's components and their serialized fields.
        /// </summary>
        [BridgeCommand("INSPECTOR", "Get component details for a GameObject",
            Category = "Component",
            Usage = "INSPECTOR Canvas/Panel  OR  INSPECTOR {\"gameObject\":\"Canvas/Panel\",\"component\":\"Transform\"}",
            RequiresMainThread = true)]
        public static string Inspector(string data)
        {
            try
            {
                string gameObjectPath;
                string filterComponent = null;

                if (data.TrimStart().StartsWith("{"))
                {
                    var json = JObject.Parse(data);
                    gameObjectPath = json["gameObject"]?.ToString();
                    filterComponent = json["component"]?.ToString();
                }
                else
                {
                    gameObjectPath = data.Trim();
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("GameObject path is required");

                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return Response.Error($"GameObject not found: {gameObjectPath}");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"GameObject: {gameObjectPath}");
                sb.AppendLine($"active: {go.activeSelf}");
                sb.AppendLine($"layer: {LayerMask.LayerToName(go.layer)}");
                sb.AppendLine($"tag: {go.tag}");
                sb.AppendLine("---");

                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (!string.IsNullOrEmpty(filterComponent) &&
                        !typeName.Equals(filterComponent, StringComparison.OrdinalIgnoreCase))
                        continue;

                    sb.AppendLine($"[{typeName}]");

                    // Use SerializedObject to read all visible fields (same as Unity Inspector)
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    if (prop.NextVisible(true))
                    {
                        do
                        {
                            sb.AppendLine($"  {prop.name}: {GetPropertyValue(prop)}");
                        } while (prop.NextVisible(false));
                    }
                    sb.AppendLine();
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static string GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("G");
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Enum: return prop.enumDisplayNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                    ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "None";
                case SerializedPropertyType.Vector2: return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return prop.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return prop.vector4Value.ToString();
                case SerializedPropertyType.Rect: return prop.rectValue.ToString();
                case SerializedPropertyType.Color: return prop.colorValue.ToString();
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                default: return $"({prop.propertyType})";
            }
        }

        private static Type FindType(string name)
        {
            // Try common Unity namespaces first
            var type = Type.GetType($"UnityEngine.{name}, UnityEngine")
                    ?? Type.GetType($"UnityEngine.UI.{name}, UnityEngine.UI")
                    ?? Type.GetType($"UnityEngine.{name}, UnityEngine.CoreModule");

            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (type != null) return type;
            }
            return null;
        }

        private static object ParseValue(JToken valueToken, Type targetType)
        {
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return null;

            // If target is a Unity Object (GameObject, Component, etc.)
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                string path = valueToken.ToString();

                // Try to find as GameObject first
                var go = GameObject.Find(path);
                if (go != null)
                {
                    // If target type is GameObject, return it
                    if (targetType == typeof(GameObject))
                        return go;

                    // If target type is a Component, try to get it
                    if (typeof(Component).IsAssignableFrom(targetType))
                    {
                        var component = go.GetComponent(targetType);
                        if (component != null)
                            return component;
                    }
                }

                // Try to load as asset
                var asset = AssetDatabase.LoadAssetAtPath(path, targetType);
                if (asset != null)
                    return asset;

                return null;
            }

            // For primitive types, convert the token
            if (targetType == typeof(string))
                return valueToken.ToString();
            if (targetType == typeof(int))
                return valueToken.ToObject<int>();
            if (targetType == typeof(float))
                return valueToken.ToObject<float>();
            if (targetType == typeof(bool))
                return valueToken.ToObject<bool>();
            if (targetType == typeof(double))
                return valueToken.ToObject<double>();

            // For other types, try to deserialize
            return valueToken.ToObject(targetType);
        }
    }
}
