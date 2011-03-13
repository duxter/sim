/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Framework.Communications;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using OpenSim.Tests.Common.Mock;
using OpenSim.Tests.Common.Setup;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Archiver.Tests
{
    [TestFixture]
    public class InventoryArchiveTestCase
    {
        protected ManualResetEvent mre = new ManualResetEvent(false);
        
        /// <summary>
        /// A raw array of bytes that we'll use to create an IAR memory stream suitable for isolated use in each test.
        /// </summary>
        protected byte[] m_iarStreamBytes;
                
        /// <summary>
        /// Stream of data representing a common IAR for load tests.
        /// </summary>
        protected MemoryStream m_iarStream;
        
        protected UserAccount m_uaMT 
            = new UserAccount { 
                PrincipalID = UUID.Parse("00000000-0000-0000-0000-000000000555"),
                FirstName = "Mr",
                LastName = "Tiddles" };
        protected UserAccount m_uaLL1
            = new UserAccount { 
                PrincipalID = UUID.Parse("00000000-0000-0000-0000-000000000666"),
                FirstName = "Lord",
                LastName = "Lucan" }; 
        protected UserAccount m_uaLL2
            = new UserAccount { 
                PrincipalID = UUID.Parse("00000000-0000-0000-0000-000000000777"),
                FirstName = "Lord",
                LastName = "Lucan" };         
        protected string m_item1Name = "Ray Gun Item";
        
        [SetUp]
        public virtual void SetUp()
        {
            m_iarStream = new MemoryStream(m_iarStreamBytes);
        }
        
        [TestFixtureSetUp]
        public void FixtureSetup()
        {
            ConstructDefaultIarBytesForTestLoad();
        }
        
        protected void ConstructDefaultIarBytesForTestLoad()
        {
//            log4net.Config.XmlConfigurator.Configure();
            
            InventoryArchiverModule archiverModule = new InventoryArchiverModule();
            Scene scene = SceneSetupHelpers.SetupScene("Inventory");
            SceneSetupHelpers.SetupSceneModules(scene, archiverModule);            
            
            UserProfileTestUtils.CreateUserWithInventory(scene, m_uaLL1, "hampshire");

            MemoryStream archiveWriteStream = new MemoryStream();
            
            // Create asset
            SceneObjectGroup object1;
            SceneObjectPart part1;
            {
                string partName = "Ray Gun Object";
                UUID ownerId = UUID.Parse("00000000-0000-0000-0000-000000000040");
                PrimitiveBaseShape shape = PrimitiveBaseShape.CreateSphere();
                Vector3 groupPosition = new Vector3(10, 20, 30);
                Quaternion rotationOffset = new Quaternion(20, 30, 40, 50);
                Vector3 offsetPosition = new Vector3(5, 10, 15);

                part1
                    = new SceneObjectPart(
                        ownerId, shape, groupPosition, rotationOffset, offsetPosition);
                part1.Name = partName;

                object1 = new SceneObjectGroup(part1);
                scene.AddNewSceneObject(object1, false);
            }

            UUID asset1Id = UUID.Parse("00000000-0000-0000-0000-000000000060");
            AssetBase asset1 = AssetHelpers.CreateAsset(asset1Id, object1);
            scene.AssetService.Store(asset1);            

            // Create item
            InventoryItemBase item1 = new InventoryItemBase();
            item1.Name = m_item1Name;
            item1.ID = UUID.Parse("00000000-0000-0000-0000-000000000020");            
            item1.AssetID = asset1.FullID;
            item1.GroupID = UUID.Random();
            item1.CreatorIdAsUuid = m_uaLL1.PrincipalID;
            item1.Owner = m_uaLL1.PrincipalID;
            item1.Folder = scene.InventoryService.GetRootFolder(m_uaLL1.PrincipalID).ID;            
            scene.AddInventoryItem(item1);
            
            archiverModule.ArchiveInventory(
                Guid.NewGuid(), m_uaLL1.FirstName, m_uaLL1.LastName, m_item1Name, "hampshire", archiveWriteStream);            
            
            m_iarStreamBytes = archiveWriteStream.ToArray();
        }
        
        protected void SaveCompleted(
            Guid id, bool succeeded, UserAccount userInfo, string invPath, Stream saveStream, 
            Exception reportedException)
        {
            mre.Set();
        }        
    }
}