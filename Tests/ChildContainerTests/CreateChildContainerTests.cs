using System.Collections.Generic;
using NUnit.Framework;
using UJect;
using UJect.Exceptions;

namespace Tests.ChildContainerTests
{
    [TestFixture]
    public class CreateChildContainerTests
    {
        [Test]
        public void TestDefaultIdAcquire()
        {
            using var parentContainer = new DiContainer("Parent");

            var ids = new HashSet<ContainerId>();
            for (int i = 0; i < 5; i++)
            {
                var newId = parentContainer.AcquireNewId();
                var addedToSet = ids.Add(newId); // If not added, the new ID wasn't unique
                Assert.IsTrue(addedToSet, $"New ID {newId} should have been added, but wasn't");
            }
        }
        
        [Test]
        public void TestIdAcquireAfterCustomId()
        {
            using var parentContainer = new DiContainer("Parent");

            var customId = new ContainerId(123);
            
            // This should cause the next id to be customId + 1
            parentContainer.CreateChildContainer(customId); 

            var nextId = parentContainer.AcquireNewId();
            Assert.That(nextId.ID, Is.EqualTo(customId.ID + 1));
            
            var customId2 = new ContainerId(456);
            
            // This should cause the next id to be customId2 + 1
            parentContainer.CreateChildContainer(customId2); 
            
            var nextId2 = parentContainer.AcquireNewId();
            Assert.That(nextId2.ID, Is.EqualTo(customId2.ID + 1));

        }
        
        
        [TestCase(1)]
        [TestCase(5)] // Just for dispose iteration testing
        [Test]
        public void TestCreateDefaultChildContainer(int numChildren)
        {
            using var parentContainer = new DiContainer("Parent");

            DiContainer previousChild = null;
            for (int i = 0; i < numChildren; i++)
            {
                using var childContainer = parentContainer.CreateChildContainer("Child");
                Assert.IsNotNull(childContainer, "childContainer != null");
                Assert.IsFalse(ReferenceEquals(previousChild, childContainer));
                
                Assert.AreEqual("Child", childContainer.ContainerName);
                
                Assert.IsTrue(parentContainer.TryGetChildContainer(childContainer.ContainerId, out var retrievedChild), "Should have been able to retrieve child container");
                Assert.IsNotNull(retrievedChild);
                Assert.IsTrue(ReferenceEquals(childContainer, retrievedChild), "childContainer and retrievedChild should be same reference");
                
                previousChild = childContainer;
            }
        
        }

        [Test]
        public void TestDisposingChildRemovesFromParent()
        {
            var parentContainer = new DiContainer("Parent");
            var childContainer = parentContainer.CreateChildContainer("Child");
            Assert.IsTrue(parentContainer.TryGetChildContainer(childContainer.ContainerId, out _), "Should have been able to retrieve child container");
            childContainer.Dispose();
            Assert.IsFalse(parentContainer.TryGetChildContainer(childContainer.ContainerId, out _), "Should no longer be able to retrieve child container");
        }
        
        [Test]
        public void TestDisposingChildDoesNotDisposeParent()
        {
            var parentContainer = new DiContainer("Parent");
            var childContainer = parentContainer.CreateChildContainer("Child");
            childContainer.Dispose();
            Assert.IsFalse(parentContainer.IsDisposed, "Parent should not be disposed");
        }
        
        [TestCase(1)]
        [TestCase(5)] // Just for dispose iteration testing
        [Test]
        public void TestDisposingParentDisposesChildren(int n)
        {
            var parentContainer = new DiContainer("Parent");
            var children = new List<DiContainer>();
            for (int i = 0; i < n; i++)
            {
                var childContainer = parentContainer.CreateChildContainer("Child");
                children.Add(childContainer);
            }

            parentContainer.Dispose();
            Assert.IsTrue(parentContainer.IsDisposed);
            foreach (var child in children)
            {
                Assert.IsTrue(child.IsDisposed);
            }
        }
        
        [Test]
        public void TestCreateTwoChildContainersWithSameIdFails()
        {
            var parentContainer = new DiContainer("Parent");

            var childId = parentContainer.AcquireNewId();
            
            parentContainer.CreateChildContainer(childId, "Child1");
            Assert.Throws<DuplicateContainerIdException>(() => parentContainer.CreateChildContainer(childId, "Child2"));
        }
        
        [Test]
        public void TestCreateTwoChildContainersWithSameIdSucceedsIfFirstIsDisposed()
        {
            var parentContainer = new DiContainer("Parent");
            var childId = parentContainer.AcquireNewId();
            
            var childContainer1 = parentContainer.CreateChildContainer(childId, "Child1");
            
            childContainer1.Dispose();
            
            Assert.DoesNotThrow(() => parentContainer.CreateChildContainer(childId, "Child2"));
        }
    }
}