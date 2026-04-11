using System;
using System.Text.Json.Serialization.Metadata;

namespace PullSub.Core
{
    /// <summary>
    /// Interface to define topic name, type, and codec in one place.
    /// Prevents topic name typos at compile time and ensures codec consistency.
    /// </summary>
    public interface ITopic<T>
    {
        string TopicName { get; }
        IPayloadCodec<T> Codec { get; }
    }

    public static class PullSubTopic
    {
        /// <summary>
        /// Creates a topic with the default codec (JSON envelope, reflection).
        /// </summary>
        public static ITopic<T> Create<T>(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));

            return new TopicDefinition<T>(topicName, JsonPayloadCodec<T>.Default);
        }

        /// <summary>
        /// Creates a topic with a specified codec.
        /// </summary>
        public static ITopic<T> Create<T>(string topicName, IPayloadCodec<T> codec)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            return new TopicDefinition<T>(topicName, codec);
        }

        /// <summary>
        /// Creates a topic using Source Generator's JsonTypeInfo with JSON envelope format.
        /// Prevents code stripping in IL2CPP builds and completely avoids reflection.
        /// </summary>
        public static ITopic<T> Create<T>(string topicName, JsonTypeInfo<T> typeInfo)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));
            if (typeInfo == null)
                throw new ArgumentNullException(nameof(typeInfo));

            return new TopicDefinition<T>(topicName, new JsonPayloadCodec<T>(typeInfo));
        }

        /// <summary>
        /// Creates a topic with flat JSON format (reflection).
        /// Payload format is <c>{ "timestamp": "...", ...T properties... }</c>.
        /// </summary>
        public static ITopic<T> CreateFlat<T>(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));

            return new TopicDefinition<T>(topicName, FlatJsonPayloadCodec<T>.Default);
        }

        /// <summary>
        /// Creates a topic using Source Generator's JsonTypeInfo with flat JSON format.
        /// Prevents code stripping in IL2CPP builds and completely avoids reflection.
        /// </summary>
        public static ITopic<T> CreateFlat<T>(string topicName, JsonTypeInfo<T> typeInfo)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));
            if (typeInfo == null)
                throw new ArgumentNullException(nameof(typeInfo));

            return new TopicDefinition<T>(topicName, new FlatJsonPayloadCodec<T>(typeInfo));
        }

        private sealed class TopicDefinition<T> : ITopic<T>
        {
            public TopicDefinition(string topicName, IPayloadCodec<T> codec)
            {
                TopicName = topicName;
                Codec = codec;
            }

            public string TopicName { get; }
            public IPayloadCodec<T> Codec { get; }
        }
    }
}