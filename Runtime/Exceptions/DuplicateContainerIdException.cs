using System;

namespace UJect.Exceptions
{
    public class DuplicateContainerIdException : ArgumentException
    {
        public readonly ContainerId ContainerId;
        public readonly DiContainer ExistingContainerWithId;

        public DuplicateContainerIdException(ContainerId containerId, DiContainer existingContainerWithId) : base($"Child Container with ID {containerId} already exists with name \"{existingContainerWithId.ContainerName}\"")
        {
            ContainerId = containerId;
            ExistingContainerWithId = existingContainerWithId;
        }
    }
}