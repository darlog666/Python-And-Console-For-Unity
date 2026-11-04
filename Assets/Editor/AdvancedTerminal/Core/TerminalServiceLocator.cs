using System;
using System.Collections.Concurrent;
using UnityEditor;

namespace AdvancedTerminal.Core
{
    public interface ITerminalService
    {
        string ServiceName { get; }
        void Teardown();
    }

    public sealed class TerminalServiceLocator : IDisposable
    {
        private static readonly Lazy<TerminalServiceLocator> sharedInstance =
            new Lazy<TerminalServiceLocator>(() => new TerminalServiceLocator());

        private readonly ConcurrentDictionary<Type, ITerminalService> registry =
            new ConcurrentDictionary<Type, ITerminalService>();

        private readonly object registrationGate = new object();

        private bool disposed;

        public static TerminalServiceLocator Current => sharedInstance.Value;

        public event Action<Type> ServiceRegistered;

        public event Action<Type> ServiceUnregistered;

        private TerminalServiceLocator()
        {
            EditorApplication.quitting += HandleEditorQuitting;
        }

        public T Register<T>(T service) where T : class
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            lock (registrationGate)
            {
                if (!registry.TryAdd(typeof(T), service))
                {
                    throw new InvalidOperationException(
                        $"Service contract '{typeof(T).Name}' is already registered.");
                }
            }

            ServiceRegistered?.Invoke(typeof(T));
            return service;
        }

        public T Resolve<T>() where T : class
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TerminalServiceLocator));
            }

            if (registry.TryGetValue(typeof(T), out ITerminalService service))
            {
                return service as T;
            }

            throw KeyNotFoundExceptionMessage<T>.BuildException();
        }

        public bool TryResolve<T>(out T service) where T : class
        {
            service = null;

            if (disposed)
            {
                return false;
            }

            if (registry.TryGetValue(typeof(T), out ITerminalService stored))
            {
                service = stored as T;
                return service != null;
            }

            return false;
        }

        public bool IsRegistered<T>() where T : class
        {
            return registry.ContainsKey(typeof(T));
        }

        public void Unregister<T>() where T : class
        {
            lock (registrationGate)
            {
                if (registry.TryRemove(typeof(T), out ITerminalService service))
                {
                    service.Teardown();
                    ServiceUnregistered?.Invoke(typeof(T));
                }
            }
        }

        private void HandleEditorQuitting()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            EditorApplication.quitting -= HandleEditorQuitting;

            lock (registrationGate)
            {
                foreach (Type contract in registry.Keys)
                {
                    if (registry.TryRemove(contract, out ITerminalService service))
                    {
                        service.Teardown();
                        ServiceUnregistered?.Invoke(contract);
                    }
                }
            }
        }

        private static class KeyNotFoundExceptionMessage<T>
        {
            public static Exception BuildException()
            {
                return new InvalidOperationException(
                    $"Service contract '{typeof(T).Name}' has not been registered " +
                    "within the TerminalServiceLocator. Verify bootstrapping order in TerminalEngine.");
            }
        }
    }
}
