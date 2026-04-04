using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;

namespace PullSub.Bridge
{
    public interface IPullSubTopicCatalogProvider
    {
        void Register(PullSubTopicCatalogBuilder builder);
    }

    public sealed class PullSubTopicCatalogBuilder
    {
        private readonly Dictionary<string, IPullSubTopicRegistration> _entries;

        internal PullSubTopicCatalogBuilder(Dictionary<string, IPullSubTopicRegistration> entries)
        {
            _entries = entries;
        }

        public void Add<T>(PullSubTopicKey key, IPullSubTopic<T> topic)
        {
            if (key.IsEmpty)
                throw new ArgumentException("Topic key is required.", nameof(key));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (_entries.ContainsKey(key.Value))
                throw new InvalidOperationException($"Duplicate topic key '{key}' detected while building topic catalog.");

            _entries[key.Value] = new PullSubTopicRegistration<T>(key, topic);
        }
    }

    public interface IPullSubTopicRegistration
    {
        PullSubTopicKey Key { get; }

        Task<IPullSubSubscriptionLease> SubscribeAsync(
            PullSubContext context,
            PullSubQualityOfServiceLevel qos,
            CancellationToken cancellationToken);

        Task SubscribeAsync(PullSubRuntime runtime, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken);
        Task UnsubscribeAsync(PullSubRuntime runtime, CancellationToken cancellationToken);
    }

    internal sealed class PullSubTopicRegistration<T> : IPullSubTopicRegistration
    {
        private readonly IPullSubTopic<T> _topic;

        public PullSubTopicRegistration(PullSubTopicKey key, IPullSubTopic<T> topic)
        {
            Key = key;
            _topic = topic;
        }

        public PullSubTopicKey Key { get; }

        public async Task<IPullSubSubscriptionLease> SubscribeAsync(
            PullSubContext context,
            PullSubQualityOfServiceLevel qos,
            CancellationToken cancellationToken)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return await context.SubscribeAsync(_topic, qos, cancellationToken).ConfigureAwait(false);
        }

        public Task SubscribeAsync(PullSubRuntime runtime, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken)
        {
            return runtime.SubscribeDataAsync(_topic, qos, cancellationToken);
        }

        public Task UnsubscribeAsync(PullSubRuntime runtime, CancellationToken cancellationToken)
        {
            return runtime.UnsubscribeDataAsync(_topic, cancellationToken);
        }
    }

    public static class PullSubTopicCatalog
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, IPullSubTopicRegistration> _entries;
        private static bool _initialized;

        public static IReadOnlyCollection<IPullSubTopicRegistration> Entries
        {
            get
            {
                EnsureInitialized();
                return _entries.Values.ToArray();
            }
        }

        public static bool TryGet(PullSubTopicKey key, out IPullSubTopicRegistration registration)
        {
            EnsureInitialized();
            return _entries.TryGetValue(key.Value, out registration);
        }

        internal static bool TryGet(string key, out IPullSubTopicRegistration registration)
        {
            EnsureInitialized();
            return _entries.TryGetValue(key ?? string.Empty, out registration);
        }

        public static void Reload()
        {
            lock (Gate)
            {
                _initialized = false;
                _entries = null;
            }
        }

        private static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (_initialized)
                    return;

                var entries = new Dictionary<string, IPullSubTopicRegistration>(StringComparer.Ordinal);
                var builder = new PullSubTopicCatalogBuilder(entries);

                foreach (var provider in CreateProviders())
                    provider.Register(builder);

                _entries = entries;
                _initialized = true;
            }
        }

        private static IEnumerable<IPullSubTopicCatalogProvider> CreateProviders()
        {
            const string generatedProviderTypeName = "PullSub.Generated.PullSubGeneratedTopicCatalogProvider";
            var providerContract = typeof(IPullSubTopicCatalogProvider);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
                    continue;

                Type providerType;
                try
                {
                    providerType = assembly.GetType(generatedProviderTypeName, throwOnError: false, ignoreCase: false);
                }
                catch
                {
                    continue;
                }

                if (providerType == null || providerType.IsAbstract || providerType.IsInterface)
                    continue;

                if (!providerContract.IsAssignableFrom(providerType))
                    continue;

                if (providerType.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                IPullSubTopicCatalogProvider provider;
                try
                {
                    provider = (IPullSubTopicCatalogProvider)Activator.CreateInstance(providerType);
                }
                catch
                {
                    continue;
                }

                if (provider != null)
                {
                    yield return provider;
                    yield break;
                }
            }

            // Compatibility fallback: if generated provider is unavailable,
            // discover any manually implemented providers like the previous behavior.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
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
                    if (type == null || type.IsAbstract || type.IsInterface)
                        continue;

                    if (string.Equals(type.FullName, generatedProviderTypeName, StringComparison.Ordinal))
                        continue;

                    if (!providerContract.IsAssignableFrom(type))
                        continue;

                    if (type.GetConstructor(Type.EmptyTypes) == null)
                        continue;

                    IPullSubTopicCatalogProvider provider;
                    try
                    {
                        provider = (IPullSubTopicCatalogProvider)Activator.CreateInstance(type);
                    }
                    catch
                    {
                        continue;
                    }

                    if (provider != null)
                        yield return provider;
                }
            }
        }
    }
}
