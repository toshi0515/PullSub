using System;
using System.Collections.Generic;
using System.Reflection;

namespace PullSub.Core
{
    internal static class ObjectMemberCopier<T>
    {
        private static readonly PropertyInfo[] WritableProperties = CollectWritableProperties();
        private static readonly FieldInfo[] WritableFields = CollectWritableFields();

        public static void Copy(T source, T destination)
        {
            if (ReferenceEquals(source, destination))
                return;

            foreach (var property in WritableProperties)
                property.SetValue(destination, property.GetValue(source));

            foreach (var field in WritableFields)
                field.SetValue(destination, field.GetValue(source));
        }

        private static PropertyInfo[] CollectWritableProperties()
        {
            var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
            var writable = new List<PropertyInfo>(properties.Length);
            foreach (var property in properties)
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;

                if (property.GetIndexParameters().Length != 0)
                    continue;

                writable.Add(property);
            }

            return writable.ToArray();
        }

        private static FieldInfo[] CollectWritableFields()
        {
            var fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public);
            var writable = new List<FieldInfo>(fields.Length);
            foreach (var field in fields)
            {
                if (field.IsInitOnly)
                    continue;

                writable.Add(field);
            }

            return writable.ToArray();
        }
    }
}
