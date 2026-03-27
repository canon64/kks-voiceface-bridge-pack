using System;

namespace MainGameSubtitleCore
{
    public enum SubtitleBackend
    {
        Auto = 0,
        InformationUI = 1,
        Overlay = 2
    }

    public sealed class SubtitleRequest
    {
        public string Text;
        public float? HoldSeconds;
        public SubtitleBackend? Backend;
        public string DisplayMode;

        internal SubtitleRequest Clone()
        {
            return new SubtitleRequest
            {
                Text = Text,
                HoldSeconds = HoldSeconds,
                Backend = Backend,
                DisplayMode = DisplayMode
            };
        }
    }

    public interface ISubtitleRequestSink
    {
        bool EnqueueSubtitle(SubtitleRequest request, out string reason);
    }

    public static class SubtitleApi
    {
        private static readonly object Sync = new object();
        private static ISubtitleRequestSink _sink;

        public static bool IsAvailable
        {
            get
            {
                lock (Sync)
                {
                    return _sink != null;
                }
            }
        }

        public static bool TryShow(string text, float holdSeconds = -1f)
        {
            var req = new SubtitleRequest
            {
                Text = text,
                HoldSeconds = holdSeconds > 0f ? (float?)holdSeconds : null
            };
            return TryShow(req, out _);
        }

        public static bool TryShow(
            string text,
            SubtitleBackend backend,
            string displayMode = null,
            float holdSeconds = -1f)
        {
            var req = new SubtitleRequest
            {
                Text = text,
                Backend = backend,
                DisplayMode = displayMode,
                HoldSeconds = holdSeconds > 0f ? (float?)holdSeconds : null
            };
            return TryShow(req, out _);
        }

        public static bool TryShow(SubtitleRequest request, out string reason)
        {
            if (request == null)
            {
                reason = "request is null";
                return false;
            }

            ISubtitleRequestSink sink;
            lock (Sync)
            {
                sink = _sink;
            }

            if (sink == null)
            {
                reason = "subtitle provider is not available";
                return false;
            }

            return sink.EnqueueSubtitle(request, out reason);
        }

        internal static void Register(ISubtitleRequestSink sink)
        {
            lock (Sync)
            {
                _sink = sink;
            }
        }

        internal static void Unregister(ISubtitleRequestSink sink)
        {
            lock (Sync)
            {
                if (ReferenceEquals(_sink, sink))
                {
                    _sink = null;
                }
            }
        }
    }
}

