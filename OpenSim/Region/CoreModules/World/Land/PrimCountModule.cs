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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.World.Land
{
    public class ParcelCounts
    {
        public int Owner = 0;
        public int Group = 0;
        public int Others = 0;
        public Dictionary <UUID, int> Users =
                new Dictionary <UUID, int>();
    }

    public class PrimCountModule : IPrimCountModule, INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_Scene;
        private Dictionary<UUID, PrimCounts> m_PrimCounts =
                new Dictionary<UUID, PrimCounts>();
        private Dictionary<UUID, UUID> m_OwnerMap =
                new Dictionary<UUID, UUID>();
        private Dictionary<UUID, int> m_SimwideCounts =
                new Dictionary<UUID, int>();
        private Dictionary<UUID, ParcelCounts> m_ParcelCounts =
                new Dictionary<UUID, ParcelCounts>();

        // For now, a simple simwide taint to get this up. Later parcel based
        // taint to allow recounting a parcel if only ownership has changed
        // without recounting the whole sim.
        private bool m_Tainted = true;
        private Object m_TaintLock = new Object();

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
        }

        public void AddRegion(Scene scene)
        {
            m_Scene = scene;

            m_Scene.EventManager.OnParcelPrimCountAdd +=
                    OnParcelPrimCountAdd;
            m_Scene.EventManager.OnObjectBeingRemovedFromScene +=
                    OnObjectBeingRemovedFromScene;
            m_Scene.EventManager.OnParcelPrimCountTainted +=
                    OnParcelPrimCountTainted;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {            
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "PrimCountModule"; }
        }

        private void OnParcelPrimCountAdd(SceneObjectGroup obj)
        {
            // If we're tainted already, don't bother to add. The next
            // access will cause a recount anyway
            lock (m_TaintLock)
            {
                if (!m_Tainted)
                    AddObject(obj);
            }
        }

        private void OnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            // Don't bother to update tainted counts
            lock (m_TaintLock)
            {
                if (!m_Tainted)
                    RemoveObject(obj);
            }
        }

        private void OnParcelPrimCountTainted()
        {
            lock (m_TaintLock)
                m_Tainted = true;
        }

        public void TaintPrimCount(ILandObject land)
        {
            lock (m_TaintLock)
                m_Tainted = true;
        }

        public void TaintPrimCount(int x, int y)
        {
            lock (m_TaintLock)
                m_Tainted = true;
        }

        public void TaintPrimCount()
        {
            lock (m_TaintLock)
                m_Tainted = true;
        }

        // NOTE: Call under Taint Lock
        private void AddObject(SceneObjectGroup obj)
        {
            if (obj.IsAttachment)
                return;
            if (((obj.RootPart.Flags & PrimFlags.TemporaryOnRez) != 0))
                return;

            Vector3 pos = obj.AbsolutePosition;
            ILandObject landObject = m_Scene.LandChannel.GetLandObject(pos.X, pos.Y);
            LandData landData = landObject.LandData;

            ParcelCounts parcelCounts;
            if (m_ParcelCounts.TryGetValue(landData.GlobalID, out parcelCounts))
            {
                UUID landOwner = landData.OwnerID;
                int partCount = obj.Parts.Length;

                m_SimwideCounts[landOwner] += partCount;
                if (parcelCounts.Users.ContainsKey(obj.OwnerID))
                    parcelCounts.Users[obj.OwnerID] += partCount;
                else
                    parcelCounts.Users[obj.OwnerID] = partCount;

                if (landData.IsGroupOwned)
                {
                    if (obj.OwnerID == landData.GroupID)
                        parcelCounts.Owner += partCount;
                    else if (obj.GroupID == landData.GroupID)
                        parcelCounts.Group += partCount;
                    else
                        parcelCounts.Others += partCount;
                }
                else
                {
                    if (obj.OwnerID == landData.OwnerID)
                        parcelCounts.Owner += partCount;
                    else if (obj.GroupID == landData.GroupID)
                        parcelCounts.Group += partCount;
                    else
                        parcelCounts.Others += partCount;
                }
            }
        }

        // NOTE: Call under Taint Lock
        private void RemoveObject(SceneObjectGroup obj)
        {
        }

        public IPrimCounts GetPrimCounts(UUID parcelID)
        {
            PrimCounts primCounts;

            lock (m_PrimCounts)
            {
                if (m_PrimCounts.TryGetValue(parcelID, out primCounts))
                    return primCounts;

                primCounts = new PrimCounts(parcelID, this);
                m_PrimCounts[parcelID] = primCounts;
            }
            return primCounts;
        }

        public int GetOwnerCount(UUID parcelID)
        {
            lock (m_TaintLock)
            {
                if (m_Tainted)
                    Recount();

                ParcelCounts counts;
                if (m_ParcelCounts.TryGetValue(parcelID, out counts))
                    return counts.Owner;
            }
            return 0;
        }

        public int GetGroupCount(UUID parcelID)
        {
            lock (m_TaintLock)
            {
                if (m_Tainted)
                    Recount();

                ParcelCounts counts;
                if (m_ParcelCounts.TryGetValue(parcelID, out counts))
                    return counts.Group;
            }
            return 0;
        }

        public int GetOthersCount(UUID parcelID)
        {
            lock (m_TaintLock)
            {
                if (m_Tainted)
                    Recount();

                ParcelCounts counts;
                if (m_ParcelCounts.TryGetValue(parcelID, out counts))
                    return counts.Others;
            }
            return 0;
        }

        public int GetSimulatorCount(UUID parcelID)
        {
            lock (m_TaintLock)
            {
                if (m_Tainted)
                    Recount();
                
                UUID owner;
                if (m_OwnerMap.TryGetValue(parcelID, out owner))
                {
                    int val;
                    if (m_SimwideCounts.TryGetValue(owner, out val))
                        return val;
                }
            }
            return 0;
        }

        public int GetUserCount(UUID parcelID, UUID userID)
        {
            lock (m_TaintLock)
            {
                if (m_Tainted)
                    Recount();

                ParcelCounts counts;
                if (m_ParcelCounts.TryGetValue(parcelID, out counts))
                {
                    int val;
                    if (counts.Users.TryGetValue(userID, out val))
                        return val;
                }
            }
            return 0;
        }

        // NOTE: This method MUST be called while holding the taint lock!
        private void Recount()
        {
            m_OwnerMap.Clear();
            m_SimwideCounts.Clear();
            m_ParcelCounts.Clear();

            List<ILandObject> land = m_Scene.LandChannel.AllParcels();

            foreach (ILandObject l in land)
            {
                LandData landData = l.LandData;

                m_OwnerMap[landData.GlobalID] = landData.OwnerID;
                m_SimwideCounts[landData.OwnerID] = 0;
                m_ParcelCounts[landData.GlobalID] = new ParcelCounts();
            }

            m_Scene.ForEachSOG(AddObject);

            List<UUID> primcountKeys = new List<UUID>(m_PrimCounts.Keys);
            foreach (UUID k in primcountKeys)
            {
                if (!m_OwnerMap.ContainsKey(k))
                    m_PrimCounts.Remove(k);
            }
            m_Tainted = false;
        }
    }

    public class PrimCounts : IPrimCounts
    {
        private PrimCountModule m_Parent;
        private UUID m_ParcelID;
        private UserPrimCounts m_UserPrimCounts;

        public PrimCounts (UUID parcelID, PrimCountModule parent)
        {
            m_ParcelID = parcelID;
            m_Parent = parent;

            m_UserPrimCounts = new UserPrimCounts(this);
        }

        public int Owner
        {
            get
            {
                return m_Parent.GetOwnerCount(m_ParcelID);
            }
        }

        public int Group
        {
            get
            {
                return m_Parent.GetGroupCount(m_ParcelID);
            }
        }

        public int Others
        {
            get
            {
                return m_Parent.GetOthersCount(m_ParcelID);
            }
        }

        public int Simulator
        {
            get
            {
                return m_Parent.GetSimulatorCount(m_ParcelID);
            }
        }

        public IUserPrimCounts Users
        {
            get
            {
                return m_UserPrimCounts;
            }
        }

        public int GetUserCount(UUID userID)
        {
            return m_Parent.GetUserCount(m_ParcelID, userID);
        }
    }

    public class UserPrimCounts : IUserPrimCounts
    {
        private PrimCounts m_Parent;

        public UserPrimCounts(PrimCounts parent)
        {
            m_Parent = parent;
        }

        public int this[UUID userID]
        {
            get
            {
                return m_Parent.GetUserCount(userID);
            }
        }
    }
}
