using System;
using System.Linq;
using PullSub.Bridge;
using UnityEditor;
using UnityEngine;

namespace PullSub.Editor
{
    [CustomPropertyDrawer(typeof(PullSubTopicKey))]
    internal sealed class PullSubTopicKeyDrawer : PropertyDrawer
    {
        private const float HelpBoxPadding = 2f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var valueProperty = property.FindPropertyRelative("_value");
            if (valueProperty == null)
            {
                EditorGUI.LabelField(position, label.text, "Invalid PullSubTopicKey field.");
                return;
            }

            var keys = GetCatalogKeys();
            var current = valueProperty.stringValue ?? string.Empty;
            var isCurrentMissing = !string.IsNullOrEmpty(current) && Array.IndexOf(keys, current) < 0;

            var popupRect = position;
            if (isCurrentMissing)
            {
                popupRect.height = EditorGUIUtility.singleLineHeight;
            }

            if (keys.Length == 0)
            {
                EditorGUI.PropertyField(popupRect, valueProperty, label);

                if (isCurrentMissing)
                {
                    var helpRect = new Rect(
                        position.x,
                        popupRect.yMax + HelpBoxPadding,
                        position.width,
                        EditorGUIUtility.singleLineHeight * 1.5f);
                    EditorGUI.HelpBox(helpRect, "Current key is missing from catalog. Keeping existing value.", MessageType.Warning);
                }

                return;
            }

            string[] popupOptions;
            var selectedIndex = 0;

            if (isCurrentMissing)
            {
                popupOptions = new string[keys.Length + 1];
                popupOptions[0] = $"<Missing> {current}";
                Array.Copy(keys, 0, popupOptions, 1, keys.Length);
            }
            else
            {
                popupOptions = keys;
                var currentIndex = Array.IndexOf(keys, current);
                selectedIndex = currentIndex >= 0 ? currentIndex : 0;
            }

            EditorGUI.BeginChangeCheck();
            var nextIndex = EditorGUI.Popup(popupRect, label.text, selectedIndex, popupOptions);
            if (EditorGUI.EndChangeCheck())
            {
                if (isCurrentMissing)
                {
                    // Index 0 keeps the current unresolved key as-is.
                    if (nextIndex > 0)
                    {
                        valueProperty.stringValue = keys[nextIndex - 1];
                    }
                }
                else
                {
                    valueProperty.stringValue = keys[nextIndex];
                }
            }

            if (isCurrentMissing)
            {
                var helpRect = new Rect(
                    position.x,
                    popupRect.yMax + HelpBoxPadding,
                    position.width,
                    EditorGUIUtility.singleLineHeight * 1.5f);
                EditorGUI.HelpBox(helpRect, "Current key is missing from catalog. Keeping existing value.", MessageType.Warning);
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var valueProperty = property.FindPropertyRelative("_value");
            if (valueProperty == null)
                return EditorGUIUtility.singleLineHeight;

            var current = valueProperty.stringValue ?? string.Empty;
            if (string.IsNullOrEmpty(current))
                return EditorGUIUtility.singleLineHeight;

            var keys = GetCatalogKeys();
            var isCurrentMissing = Array.IndexOf(keys, current) < 0;
            if (!isCurrentMissing)
                return EditorGUIUtility.singleLineHeight;

            return EditorGUIUtility.singleLineHeight + HelpBoxPadding + (EditorGUIUtility.singleLineHeight * 1.5f);
        }

        private static string[] GetCatalogKeys()
        {
            return PullSubTopicCatalog.Entries
                .Select(e => e.Key.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
