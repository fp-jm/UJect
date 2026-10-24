// Copyright (c) 2026 OopsItsACoder
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using UJect.Exceptions;
using UJect.Resolvers;
using UJect.Utilities;
using static UJect.Assertions.RuntimeAssert;

namespace UJect
{
    public sealed partial class DiContainer : IDisposable
    {
        private readonly object nextChildContainerLock = new();
        private ContainerId nextChildContainerId =default;

        public delegate void OnChildContainerStateChangedDelegate(DiContainer parentContainer, DiContainer container, StateChange stateChange);

        public event OnChildContainerStateChangedDelegate? OnChildContainerStateChanged;
        
        private readonly Dictionary<InjectionKey, IResolvedInstance> resolvedInstances = new();
        private readonly Dictionary<InjectionKey, IResolver> dependencyResolvers = new();
        private readonly Dictionary<IResolver, InjectionKey[]> allKeyLookup = new();
        private readonly DependencyTree dependencyTree = new();
        public readonly ContainerId ContainerId; 

        private Dictionary<ContainerId, DiContainer>? childContainers = null;

        public IDependencyTree DependencyTree => dependencyTree;
        
        private readonly DiContainer? parentContainer;
        private readonly string containerName;
        private readonly CancellationTokenSource cancellationTokenSource = new();

        private DiPhase phase;
        
        /// <summary>
        /// Returns true if Dispose has already been called on this <see cref="DiContainer"/>
        /// </summary>
        public bool IsDisposed { get; private set; }

        private DiPhase Phase
        {
            get => phase;
            set => phase = value;
        }
        
        /// <summary>
        /// Cancellation Token that shares a lifetime with this DiContainer
        /// </summary>
        public CancellationToken CancellationToken => cancellationTokenSource.Token;

        /// <summary>
        /// Name of DiContainer.
        /// </summary>
        public string ContainerName => containerName;

        /// <summary>
        /// Create a new DiContainer with an empty name
        /// </summary>
        public DiContainer() : this(default, null){}

        /// <summary>
        /// Create a new DiContainer with the provided name
        /// </summary>
        /// <param name="containerName"></param>
        public DiContainer(string containerName) : this(default, null, containerName){}

        /// <summary>
        /// Create a new DiContainer with the provided name
        /// </summary>
        /// <param name="containerId">Id to use for the newly created container. Should be globally unique to <b>all</b> containers</param>
        /// <param name="containerName"></param>
        public DiContainer(ContainerId containerId, string containerName) : this(containerId: containerId, parentContainer: null, containerName: containerName){}

        /// <summary>
        /// Create a new DiContainer with the provided name and parent container
        /// </summary>
        /// <param name="containerId">Id to use for the newly created container. Should be globally unique to <b>all</b> containers. If null, a new one will be created.</param>
        /// <param name="parentContainer"></param>
        /// <param name="containerName"></param>
        internal DiContainer(ContainerId containerId, DiContainer? parentContainer = null, string? containerName = null)
        {
            ContainerId = containerId;
            this.parentContainer = parentContainer;
            this.containerName = containerName ?? $"Container_{ContainerId}";
            Phase                = DiPhase.Bind;

            
            // Bind this container to itself
            BindInstance(this);
            
            // And bind the CancellationToken for this container to itself
            BindInstance(CancellationToken);

            if (this.parentContainer != null)
            {
                this.parentContainer.AddChildContainer(this);
            }
        }

        public bool TryGetChildContainer(ContainerId containerId,[MaybeNullWhen(false)] out DiContainer childContainer)
        {
            childContainer = null;
            if (childContainers == null) return false;
            return childContainers.TryGetValue(containerId, out childContainer);
        }

        private void AddChildContainer(DiContainer childContainer)
        {
            childContainers ??= new();

            if (childContainers.TryGetValue(childContainer.ContainerId, out var existingChildWithId))
            {
                throw new DuplicateContainerIdException(childContainer.ContainerId, existingChildWithId);
            }
            var finalId = GetIdForNewChild(childContainer.ContainerId);
            childContainers.Add(finalId, childContainer);
            OnChildContainerStateChanged?.Invoke(this, childContainer, StateChange.Added);
        }

        private void RemoveChildContainer(ContainerId childContainerId)
        {
            if (childContainers == null) return;
            if (childContainers.Remove(childContainerId, out var removedChild))
            {
                OnChildContainerStateChanged?.Invoke(this, removedChild, StateChange.Removed);
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }
            IsDisposed = true;
            
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            if (childContainers?.Count > 0)
            {
                var childContainerCopy = childContainers.Values.ToArray();
                foreach (var childContainer in childContainerCopy)
                {
                    childContainer.Dispose();
                }
                childContainers.Clear();
            }

            if (parentContainer != null)
            {
                parentContainer.RemoveChildContainer(this.ContainerId);
            }

            foreach (var resolvedInstance in resolvedInstances.Values)
            {
                if (!LifetimeCheck.IsNullOrDestroyed(resolvedInstance) && (resolvedInstance.InstanceObject is IDisposable disposable))
                {
                    disposable.Dispose();
                }
            }

            foreach (var dependencyResolver in dependencyResolvers.Values)
            {
                if (dependencyResolver is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            resolvedInstances.Clear();
            dependencyResolvers.Clear();
        }

        public ContainerId AcquireNewId() => GetIdForNewChild(requestedId: null);

        private ContainerId GetIdForNewChild(ContainerId? requestedId)
        {
            lock (nextChildContainerLock)
            {
                UJect.ContainerId finalValue;
                UJect.ContainerId highestValue = nextChildContainerId;
                if (requestedId.HasValue)
                {
                    finalValue = requestedId.Value;
                    if (requestedId.Value.ID > highestValue.ID)
                    {
                        highestValue = requestedId.Value;
                    }
                }
                else
                {
                    finalValue = nextChildContainerId;
                }

                var next = highestValue.Next();
                nextChildContainerId = next;
                return finalValue;
            }
        }

        /// <summary>
        /// Create a new DiContainer that is a child of this container.
        /// </summary>
        /// <param name="childContainerName">Optional Container name for the new child container. Used only for debugging.</param>
        /// <returns>The new child container</returns>
        [LibraryEntryPoint]
        public DiContainer CreateChildContainer(string? childContainerName = null)
            => CreateChildContainer(AcquireNewId(), childContainerName);
        
        /// <summary>
        /// Create a new DiContainer that is a child of this container, using a specific container ID.
        /// </summary>
        /// <param name="childContainerId">Id to use for the newly created container. Should be globally unique to <b>all</b> containers</param>
        /// <param name="childContainerName">Optional Container name for the new child container. Used only for debugging.</param>
        /// <returns>The new child container</returns>
        [LibraryEntryPoint]
        public DiContainer CreateChildContainer(ContainerId childContainerId, string? childContainerName = null) 
            => new(childContainerId, this, childContainerName);

        public override string ToString() 
            => $"{nameof(DiContainer)} \"{containerName}\"";

        private enum DiPhase : byte
        {
            Bind     = 0,
            Resolved = 1
        }

        #region Internal Binding Methods

        /// <summary>
        /// Mark depends as dependent on dependency
        /// </summary>
        /// <param name="depends"></param>
        /// <param name="dependency"></param>
        /// <exception cref="CyclicDependencyException"></exception>
        private void AddDependencies(InjectionKey depends, InjectionKey dependency)
        {
            dependencyTree.AddDependency(depends, dependency);

            if (dependencyTree.HasCycle(out var onDependency))
            {
                throw new CyclicDependencyException($"Adding dependency binding for {depends} to {dependency} resulted in a dependency cycle!\n{onDependency}");
            }
        }

        #endregion Internal Binding Methods

        #region Resolving Instances

        /// <summary>
        ///     Resolve all dependencies, if required. You shouldn't have to call this, it should happen automatically when attempting to retrieve a dependency.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        [LibraryEntryPoint]
        public void TryResolveAll()
        {
            if (Phase < DiPhase.Resolved)
            {
                Phase = DiPhase.Resolved;
                if (dependencyTree.HasCycle(out _))
                {
                    throw new InvalidOperationException("Cycle detected in dependency tree!");
                }

                foreach (var injectionKey in dependencyTree.Sorted())
                {
                    if (resolvedInstances.ContainsKey(injectionKey))
                    {
                        //Already resolved. Skip
                        continue;
                    }

                    if (dependencyResolvers.TryGetValue(injectionKey, out var resolver))
                    {
                        ResolveInstance(injectionKey, resolver);
                    }
                }

                foreach (var resolvedInstance in resolvedInstances.Select(r => r.Value))
                {
                    if (!LifetimeCheck.IsNullOrDestroyed(resolvedInstance))
                    {
                        resolvedInstance.Initialize(this);
                    }
                }
            }
        }

        /// <summary>
        ///     Resolve a specific dependency instance. The instance will have its dependencies injected
        /// </summary>
        /// <param name="injectionKey"></param>
        /// <param name="resolver"></param>
        private void ResolveInstance(InjectionKey injectionKey, IResolver resolver)
        {
            var resolvedInstance = resolver.Resolve();
            AssertObjectIsAliveFormat(resolvedInstance, "Cannot inject into dead resolved instance for key {0}", injectionKey);
            InjectInto(resolvedInstance.InstanceObject);

            if (allKeyLookup.TryGetValue(resolver, out var allKeys))
            {
                foreach (var resolvedKey in allKeys)
                {            
                    resolvedInstances.Add(resolvedKey, resolvedInstance);
                }
            }
            else
            {
                resolvedInstances.Add(injectionKey, resolvedInstance);
            }
        }

        #endregion Resolving Instances

    }

    public enum StateChange : byte
    {
        /// <summary>
        /// Unknown or bad state. Unused.
        /// </summary>
        Invalid = 0,
        /// <summary>
        ///  Child container was added to this container
        /// </summary>
        Added,
        /// <summary>
        /// Child container was removed from this container
        /// </summary>
        Removed,
    }
}
