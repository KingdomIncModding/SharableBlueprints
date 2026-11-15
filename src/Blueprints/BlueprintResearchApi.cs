using System;
using System.Collections.Generic;
using KingdomFoundation.Json;

namespace SharableBlueprints
{
    /// <summary>Optional custom-machine research reference declarations for Sharable Blueprints.</summary>
    public static class BlueprintResearchApi
    {
        private sealed class DelegateDisposable : IDisposable
        {
            private Action dispose;
            internal DelegateDisposable(Action dispose) => this.dispose = dispose;
            public void Dispose()
            {
                var action = dispose;
                dispose = null;
                action?.Invoke();
            }
        }

        private static readonly Dictionary<string, Func<JsonValue, IEnumerable<(string key, string value)>>> Extractors =
            new Dictionary<string, Func<JsonValue, IEnumerable<(string key, string value)>>>(StringComparer.Ordinal);

        /// <summary>Registers an explicit item/recipe reference extractor for a custom machine type.</summary>
        public static IDisposable RegisterReferenceExtractor(
            string elementType,
            Func<JsonValue, IEnumerable<(string key, string value)>> extractor)
        {
            if (string.IsNullOrWhiteSpace(elementType))
                throw new ArgumentException("Element type is required.", nameof(elementType));
            if (extractor == null)
                throw new ArgumentNullException(nameof(extractor));

            Extractors[elementType] = extractor;
            return new DelegateDisposable(() =>
            {
                if (Extractors.TryGetValue(elementType, out var current) && ReferenceEquals(current, extractor))
                    Extractors.Remove(elementType);
            });
        }

        internal static IEnumerable<(string key, string value)> ExtractCustomReferences(string elementType, JsonValue data)
        {
            if (string.IsNullOrEmpty(elementType) || !Extractors.TryGetValue(elementType, out var extractor))
                yield break;

            foreach (var entry in extractor(data))
            {
                if (!string.IsNullOrEmpty(entry.value))
                    yield return entry;
            }
        }
    }
}

