using System;

namespace PullSub.Core
{
    /// <summary>
    /// トピック名・型・Codec を一箇所に定義するインターフェース。
    /// トピック名のタイプミスをコンパイル時に防ぎ、Codec の統一を保証します。
    /// </summary>
    public interface IPullSubTopic<T>
    {
        string TopicName { get; }
        IPayloadCodec<T> Codec { get; }
    }

    /// <summary>
    /// IPullSubTopic の生成ヘルパー。
    /// </summary>
    public static class PullSubTopic
    {
        /// <summary>
        /// デフォルト Codec（JSON）でトピックを生成します。
        /// </summary>
        public static IPullSubTopic<T> Create<T>(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));

            return new TopicDefinition<T>(topicName, PullSubJsonPayloadCodec<T>.Default);
        }

        /// <summary>
        /// Codec を明示してトピックを生成します。
        /// </summary>
        public static IPullSubTopic<T> Create<T>(string topicName, IPayloadCodec<T> codec)
        {
            if (string.IsNullOrWhiteSpace(topicName))
                throw new ArgumentException("topicName is required.", nameof(topicName));

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            return new TopicDefinition<T>(topicName, codec);
        }

        private sealed class TopicDefinition<T> : IPullSubTopic<T>
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