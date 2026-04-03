using System;

namespace PullSub.Bridge
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class PullSubTopicCatalogIncludeAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public sealed class PullSubTopicCatalogIgnoreAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class PullSubTopicKeyAliasAttribute : Attribute
    {
        public PullSubTopicKeyAliasAttribute(string key)
        {
            Key = key;
        }

        public string Key { get; }
    }
}
