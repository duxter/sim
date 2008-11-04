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
 *     * Neither the name of the OpenSim Project nor the
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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Security.Authentication;
using System.Threading;
using OpenMetaverse;
using log4net;
using Nwc.XmlRpc;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Servers;
using OpenSim.Region.Communications.Local;

namespace OpenSim.Region.Communications.OGS1
{
    public class OGS1GridServices : IGridServices, IInterRegionCommunications
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Encapsulate local backend services for manipulation of local regions
        /// </summary>
        private LocalBackEndServices m_localBackend = new LocalBackEndServices();
        
        private Dictionary<ulong, RegionInfo> m_remoteRegionInfoCache = new Dictionary<ulong, RegionInfo>();
        // private List<SimpleRegionInfo> m_knownRegions = new List<SimpleRegionInfo>();
        private Dictionary<ulong, int> m_deadRegionCache = new Dictionary<ulong, int>();
        private Dictionary<string, string> m_queuedGridSettings = new Dictionary<string, string>();
        private List<RegionInfo> m_regionsOnInstance = new List<RegionInfo>();

        public BaseHttpServer httpListener;
        public NetworkServersInfo serversInfo;
        public BaseHttpServer httpServer;
        
        public string gdebugRegionName
        {
            get { return m_localBackend.gdebugRegionName; }
            set { m_localBackend.gdebugRegionName = value; }
        }  

        public string rdebugRegionName
        {
            get { return _rdebugRegionName; }
            set { _rdebugRegionName = value; }
        }
        private string _rdebugRegionName = String.Empty;
        
        public bool RegionLoginsEnabled
        {
            get { return m_localBackend.RegionLoginsEnabled; }
            set { m_localBackend.RegionLoginsEnabled = value; }
        }      

        /// <summary>
        /// Contructor.  Adds "expect_user" and "check" xmlrpc method handlers
        /// </summary>
        /// <param name="servers_info"></param>
        /// <param name="httpServe"></param>
        public OGS1GridServices(NetworkServersInfo servers_info, BaseHttpServer httpServe)
        {
            serversInfo = servers_info;
            httpServer = httpServe;

            //Respond to Grid Services requests
            httpServer.AddXmlRPCHandler("expect_user", ExpectUser);
            httpServer.AddXmlRPCHandler("logoff_user", LogOffUser);
            httpServer.AddXmlRPCHandler("check", PingCheckReply);
            httpServer.AddXmlRPCHandler("land_data", LandData);

            StartRemoting();
        }

        // see IGridServices
        public RegionCommsListener RegisterRegion(RegionInfo regionInfo)
        {
            m_regionsOnInstance.Add(regionInfo);

            m_log.InfoFormat(
                "[OGS1 GRID SERVICES]: Attempting to register region {0} with grid at {1}",
                regionInfo.RegionName, serversInfo.GridURL);

            Hashtable GridParams = new Hashtable();
            // Login / Authentication

            GridParams["authkey"] = serversInfo.GridSendKey;
            GridParams["recvkey"] = serversInfo.GridRecvKey;
            GridParams["UUID"] = regionInfo.RegionID.ToString();
            GridParams["sim_ip"] = regionInfo.ExternalHostName;
            GridParams["sim_port"] = regionInfo.InternalEndPoint.Port.ToString();
            GridParams["region_locx"] = regionInfo.RegionLocX.ToString();
            GridParams["region_locy"] = regionInfo.RegionLocY.ToString();
            GridParams["sim_name"] = regionInfo.RegionName;
            GridParams["http_port"] = serversInfo.HttpListenerPort.ToString();
            GridParams["remoting_port"] = NetworkServersInfo.RemotingListenerPort.ToString();
            GridParams["map-image-id"] = regionInfo.RegionSettings.TerrainImageID.ToString();
            GridParams["originUUID"] = regionInfo.originRegionID.ToString();
            GridParams["server_uri"] = regionInfo.ServerURI;
            GridParams["region_secret"] = regionInfo.regionSecret;

            if (regionInfo.MasterAvatarAssignedUUID != UUID.Zero)
                GridParams["master_avatar_uuid"] = regionInfo.MasterAvatarAssignedUUID.ToString();
            else
                GridParams["master_avatar_uuid"] = regionInfo.EstateSettings.EstateOwner.ToString();

            // Package into an XMLRPC Request
            ArrayList SendParams = new ArrayList();
            SendParams.Add(GridParams);

            // Send Request
            XmlRpcRequest GridReq = new XmlRpcRequest("simulator_login", SendParams);
            XmlRpcResponse GridResp;
            
            try
            {                
                // The timeout should always be significantly larger than the timeout for the grid server to request
                // the initial status of the region before confirming registration.
                GridResp = GridReq.Send(serversInfo.GridURL, 90000);
            }
            catch (Exception e)
            {
                Exception e2
                    = new Exception(
                        String.Format(
                            "Unable to register region with grid at {0}. Grid service not running?", 
                            serversInfo.GridURL),
                        e);

                throw e2;
            }

            Hashtable GridRespData = (Hashtable)GridResp.Value;
            // Hashtable griddatahash = GridRespData;

            // Process Response
            if (GridRespData.ContainsKey("error"))
            {
                string errorstring = (string) GridRespData["error"];

                Exception e = new Exception(
                    String.Format("Unable to connect to grid at {0}: {1}", serversInfo.GridURL, errorstring));

                throw e;
            }
            else
            {
                // m_knownRegions = RequestNeighbours(regionInfo.RegionLocX, regionInfo.RegionLocY);
                if (GridRespData.ContainsKey("allow_forceful_banlines"))
                {
                    if ((string) GridRespData["allow_forceful_banlines"] != "TRUE")
                    {
                        //m_localBackend.SetForcefulBanlistsDisallowed(regionInfo.RegionHandle);
                        m_queuedGridSettings.Add("allow_forceful_banlines", "FALSE");
                    }
                }

                m_log.InfoFormat(
                    "[OGS1 GRID SERVICES]: Region {0} successfully registered with grid at {1}",
                    regionInfo.RegionName, serversInfo.GridURL);
            }
            
            return m_localBackend.RegisterRegion(regionInfo);
        }

        // see IGridServices
        public bool DeregisterRegion(RegionInfo regionInfo)
        {
            Hashtable GridParams = new Hashtable();

            GridParams["UUID"] = regionInfo.RegionID.ToString();

            // Package into an XMLRPC Request
            ArrayList SendParams = new ArrayList();
            SendParams.Add(GridParams);

            // Send Request
            XmlRpcRequest GridReq = new XmlRpcRequest("simulator_after_region_moved", SendParams);
            XmlRpcResponse GridResp = null;
            
            try
            {            
                GridResp = GridReq.Send(serversInfo.GridURL, 10000);
            }
            catch (Exception e)
            {
                Exception e2
                    = new Exception(
                        String.Format(
                            "Unable to deregister region with grid at {0}. Grid service not running?", 
                            serversInfo.GridURL),
                        e);

                throw e2;
            }
        
            Hashtable GridRespData = (Hashtable) GridResp.Value;

            // Hashtable griddatahash = GridRespData;

            // Process Response
            if (GridRespData != null && GridRespData.ContainsKey("error"))
            {
                string errorstring = (string)GridRespData["error"];
                m_log.Error("Unable to connect to grid: " + errorstring);
                return false;
            }

            return m_localBackend.DeregisterRegion(regionInfo);
        }

        public virtual Dictionary<string, string> GetGridSettings()
        {
            Dictionary<string, string> returnGridSettings = new Dictionary<string, string>();
            lock (m_queuedGridSettings)
            {
                foreach (string Dictkey in m_queuedGridSettings.Keys)
                {
                    returnGridSettings.Add(Dictkey, m_queuedGridSettings[Dictkey]);
                }

                m_queuedGridSettings.Clear();
            }

            return returnGridSettings;
        }

        // see IGridServices
        public List<SimpleRegionInfo> RequestNeighbours(uint x, uint y)
        {
            Hashtable respData = MapBlockQuery((int) x - 1, (int) y - 1, (int) x + 1, (int) y + 1);

            List<SimpleRegionInfo> neighbours = new List<SimpleRegionInfo>();

            foreach (ArrayList neighboursList in respData.Values)
            {
                foreach (Hashtable neighbourData in neighboursList)
                {
                    uint regX = Convert.ToUInt32(neighbourData["x"]);
                    uint regY = Convert.ToUInt32(neighbourData["y"]);
                    if ((x != regX) || (y != regY))
                    {
                        string simIp = (string) neighbourData["sim_ip"];

                        uint port = Convert.ToUInt32(neighbourData["sim_port"]);
                        // string externalUri = (string) neighbourData["sim_uri"];

                        // string externalIpStr = String.Empty;
                        try
                        {
                            // externalIpStr = Util.GetHostFromDNS(simIp).ToString();
                            Util.GetHostFromDNS(simIp).ToString();
                        }
                        catch (SocketException e)
                        {
                            m_log.WarnFormat(
                                "[OGS1 GRID SERVICES]: RequestNeighbours(): Lookup of neighbour {0} failed!  Not including in neighbours list.  {1}",
                                simIp, e);

                            continue;
                        }

                        SimpleRegionInfo sri = new SimpleRegionInfo(regX, regY, simIp, port);

                        sri.RemotingPort = Convert.ToUInt32(neighbourData["remoting_port"]);

                        if (neighbourData.ContainsKey("http_port"))
                        {
                            sri.HttpPort = Convert.ToUInt32(neighbourData["http_port"]);
                        }

                        sri.RegionID = new UUID((string) neighbourData["uuid"]);

                        neighbours.Add(sri);
                    }
                }
            }

            return neighbours;
        }

        /// <summary>
        /// Request information about a region.
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <returns>
        /// null on a failure to contact or get a response from the grid server
        /// FIXME: Might be nicer to return a proper exception here since we could inform the client more about the
        /// nature of the faiulre.
        /// </returns>
        public RegionInfo RequestNeighbourInfo(UUID Region_UUID)
        {
            // don't ask the gridserver about regions on this instance...
            foreach (RegionInfo info in m_regionsOnInstance)
            {
                if (info.RegionID == Region_UUID) return info;
            }

            // didn't find it so far, we have to go the long way
            RegionInfo regionInfo;
            Hashtable requestData = new Hashtable();
            requestData["region_UUID"] = Region_UUID.ToString();
            requestData["authkey"] = serversInfo.GridSendKey;
            ArrayList SendParams = new ArrayList();
            SendParams.Add(requestData);
            XmlRpcRequest gridReq = new XmlRpcRequest("simulator_data_request", SendParams);
            XmlRpcResponse gridResp = null;

            try
            {
                gridResp = gridReq.Send(serversInfo.GridURL, 3000);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[OGS1 GRID SERVICES]: Communication with the grid server at {0} failed, {1}",
                    serversInfo.GridURL, e);

                return null;
            }

            Hashtable responseData = (Hashtable)gridResp.Value;

            if (responseData.ContainsKey("error"))
            {
                m_log.WarnFormat("[OGS1 GRID SERVICES]: Error received from grid server: {0}", responseData["error"]);
                return null;
            }

            regionInfo = buildRegionInfo(responseData, String.Empty);
            if (requestData.ContainsKey("regionHandle"))
            {
                m_remoteRegionInfoCache.Add(Convert.ToUInt64((string) requestData["regionHandle"]), regionInfo);
            }

            return regionInfo;
        }

        /// <summary>
        /// Request information about a region.
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <returns></returns>
        public RegionInfo RequestNeighbourInfo(ulong regionHandle)
        {
            RegionInfo regionInfo = m_localBackend.RequestNeighbourInfo(regionHandle);

            if (regionInfo != null)
            {
                return regionInfo;
            }

            if (!m_remoteRegionInfoCache.TryGetValue(regionHandle, out regionInfo))
            {
                try
                {
                    Hashtable requestData = new Hashtable();
                    requestData["region_handle"] = regionHandle.ToString();
                    requestData["authkey"] = serversInfo.GridSendKey;
                    ArrayList SendParams = new ArrayList();
                    SendParams.Add(requestData);
                    XmlRpcRequest GridReq = new XmlRpcRequest("simulator_data_request", SendParams);
                    XmlRpcResponse GridResp = GridReq.Send(serversInfo.GridURL, 3000);

                    Hashtable responseData = (Hashtable) GridResp.Value;

                    if (responseData.ContainsKey("error"))
                    {
                        m_log.Error("[OGS1 GRID SERVICES]: Error received from grid server: " + responseData["error"]);
                        return null;
                    }

                    uint regX = Convert.ToUInt32((string) responseData["region_locx"]);
                    uint regY = Convert.ToUInt32((string) responseData["region_locy"]);
                    string internalIpStr = (string) responseData["sim_ip"];
                    uint port = Convert.ToUInt32(responseData["sim_port"]);
                    // string externalUri = (string) responseData["sim_uri"];

                    IPEndPoint neighbourInternalEndPoint = new IPEndPoint(IPAddress.Parse(internalIpStr), (int) port);
                    // string neighbourExternalUri = externalUri;
                    regionInfo = new RegionInfo(regX, regY, neighbourInternalEndPoint, internalIpStr);

                    regionInfo.RemotingPort = Convert.ToUInt32((string) responseData["remoting_port"]);
                    regionInfo.RemotingAddress = internalIpStr;

                    if (responseData.ContainsKey("http_port"))
                    {
                        regionInfo.HttpPort = Convert.ToUInt32((string) responseData["http_port"]);
                    }

                    regionInfo.RegionID = new UUID((string) responseData["region_UUID"]);
                    regionInfo.RegionName = (string) responseData["region_name"];

                    lock (m_remoteRegionInfoCache)
                    {
                        if (!m_remoteRegionInfoCache.ContainsKey(regionHandle))
                        {
                            m_remoteRegionInfoCache.Add(regionHandle, regionInfo);
                        }
                    }
                }
                catch
                {
                    m_log.Error("[OGS1 GRID SERVICES]: " +
                                "Region lookup failed for: " + regionHandle.ToString() +
                                " - Is the GridServer down?");
                    return null;
                }
            }

            return regionInfo;
        }

        public RegionInfo RequestClosestRegion(string regionName)
        {
            foreach (RegionInfo ri in m_remoteRegionInfoCache.Values)
            {
                if (ri.RegionName == regionName)
                    return ri;
            }

            RegionInfo regionInfo = null;
            try
            {
                Hashtable requestData = new Hashtable();
                requestData["region_name_search"] = regionName;
                requestData["authkey"] = serversInfo.GridSendKey;
                ArrayList SendParams = new ArrayList();
                SendParams.Add(requestData);
                XmlRpcRequest GridReq = new XmlRpcRequest("simulator_data_request", SendParams);
                XmlRpcResponse GridResp = GridReq.Send(serversInfo.GridURL, 3000);

                Hashtable responseData = (Hashtable) GridResp.Value;

                if (responseData.ContainsKey("error"))
                {
                    m_log.ErrorFormat("[OGS1 GRID SERVICES]: Error received from grid server: ", responseData["error"]);
                    return null;
                }

                regionInfo = buildRegionInfo(responseData, "");

                if (!m_remoteRegionInfoCache.ContainsKey(regionInfo.RegionHandle))
                    m_remoteRegionInfoCache.Add(regionInfo.RegionHandle, regionInfo);
            }
            catch
            {
                m_log.Error("[OGS1 GRID SERVICES]: " +
                            "Region lookup failed for: " + regionName +
                            " - Is the GridServer down?");
            }

            return regionInfo;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="minX"></param>
        /// <param name="minY"></param>
        /// <param name="maxX"></param>
        /// <param name="maxY"></param>
        /// <returns></returns>
        public List<MapBlockData> RequestNeighbourMapBlocks(int minX, int minY, int maxX, int maxY)
        {
            int temp = 0;

            if (minX > maxX)
            {
                temp = minX;
                minX = maxX;
                maxX = temp;
            }
            if (minY > maxY)
            {
                temp = minY;
                minY = maxY;
                maxY = temp;
            }

            Hashtable respData = MapBlockQuery(minX, minY, maxX, maxY);

            List<MapBlockData> neighbours = new List<MapBlockData>();

            foreach (ArrayList a in respData.Values)
            {
                foreach (Hashtable n in a)
                {
                    MapBlockData neighbour = new MapBlockData();

                    neighbour.X = Convert.ToUInt16(n["x"]);
                    neighbour.Y = Convert.ToUInt16(n["y"]);

                    neighbour.Name = (string) n["name"];
                    neighbour.Access = Convert.ToByte(n["access"]);
                    neighbour.RegionFlags = Convert.ToUInt32(n["region-flags"]);
                    neighbour.WaterHeight = Convert.ToByte(n["water-height"]);
                    neighbour.MapImageId = new UUID((string) n["map-image-id"]);

                    neighbours.Add(neighbour);
                }
            }

            return neighbours;
        }

        /// <summary>
        /// Performs a XML-RPC query against the grid server returning mapblock information in the specified coordinates
        /// </summary>
        /// <remarks>REDUNDANT - OGS1 is to be phased out in favour of OGS2</remarks>
        /// <param name="minX">Minimum X value</param>
        /// <param name="minY">Minimum Y value</param>
        /// <param name="maxX">Maximum X value</param>
        /// <param name="maxY">Maximum Y value</param>
        /// <returns>Hashtable of hashtables containing map data elements</returns>
        private Hashtable MapBlockQuery(int minX, int minY, int maxX, int maxY)
        {
            Hashtable param = new Hashtable();
            param["xmin"] = minX;
            param["ymin"] = minY;
            param["xmax"] = maxX;
            param["ymax"] = maxY;
            IList parameters = new ArrayList();
            parameters.Add(param);
            
            try
            {
                XmlRpcRequest req = new XmlRpcRequest("map_block", parameters);
                XmlRpcResponse resp = req.Send(serversInfo.GridURL, 10000);
                Hashtable respData = (Hashtable) resp.Value;
                return respData;
            }
            catch (Exception e)
            {
                m_log.Error("MapBlockQuery XMLRPC failure: " + e);
                return new Hashtable();
            }
        }

        /// <summary>
        /// A ping / version check
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse PingCheckReply(XmlRpcRequest request)
        {
            XmlRpcResponse response = new XmlRpcResponse();

            Hashtable respData = new Hashtable();
            respData["online"] = "true";

            m_localBackend.PingCheckReply(respData);

            response.Value = respData;

            return response;
        }

        /// <summary>
        /// Received from the user server when a user starts logging in.  This call allows
        /// the region to prepare for direct communication from the client.  Sends back an empty
        /// xmlrpc response on completion.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse ExpectUser(XmlRpcRequest request)
        {            
            Hashtable requestData = (Hashtable) request.Params[0];
            AgentCircuitData agentData = new AgentCircuitData();
            agentData.SessionID = new UUID((string) requestData["session_id"]);
            agentData.SecureSessionID = new UUID((string) requestData["secure_session_id"]);
            agentData.firstname = (string) requestData["firstname"];
            agentData.lastname = (string) requestData["lastname"];
            agentData.AgentID = new UUID((string) requestData["agent_id"]);
            agentData.circuitcode = Convert.ToUInt32(requestData["circuit_code"]);
            agentData.CapsPath = (string) requestData["caps_path"];
            ulong regionHandle = Convert.ToUInt64((string) requestData["regionhandle"]);

            m_log.DebugFormat(
                "[CLIENT]: Told by user service to prepare for a connection from {0} {1} {2}, circuit {3}",
                agentData.firstname, agentData.lastname, agentData.AgentID, agentData.circuitcode);            

            if (requestData.ContainsKey("child_agent") && requestData["child_agent"].Equals("1"))
            {
                m_log.Debug("[CLIENT]: Child agent detected");
                agentData.child = true;
            }
            else
            {
                m_log.Debug("[CLIENT]: Main agent detected");
                agentData.startpos =
                    new Vector3((float)Convert.ToDecimal((string)requestData["startpos_x"]),
                                  (float)Convert.ToDecimal((string)requestData["startpos_y"]),
                                  (float)Convert.ToDecimal((string)requestData["startpos_z"]));
                agentData.child = false;
            }

            XmlRpcResponse resp = new XmlRpcResponse();
                        
            if (!RegionLoginsEnabled)
            {
                m_log.InfoFormat(
                    "[CLIENT]: Denying access for user {0} {1} because region login is currently disabled",
                    agentData.firstname, agentData.lastname);

                Hashtable respdata = new Hashtable();
                respdata["success"] = "FALSE";
                respdata["reason"] = "region login currently disabled";
                resp.Value = respdata;                
            }
            else
            {
                RegionInfo[] regions = m_regionsOnInstance.ToArray();
                bool banned = false;

                for (int i = 0; i < regions.Length; i++)
                {
                    if (regions[i] != null)
                    {
                        if (regions[i].RegionHandle == regionHandle)
                        {
                            if (regions[i].EstateSettings.IsBanned(agentData.AgentID))
                            {
                                banned = true;
                                break;
                            }
                        }
                    }
                }                
            
                if (banned)
                {
                    m_log.InfoFormat(
                        "[CLIENT]: Denying access for user {0} {1} because user is banned",
                        agentData.firstname, agentData.lastname);

                    Hashtable respdata = new Hashtable();
                    respdata["success"] = "FALSE";
                    respdata["reason"] = "banned";
                    resp.Value = respdata;
                }
                else
                {
                    m_localBackend.TriggerExpectUser(regionHandle, agentData);
                    Hashtable respdata = new Hashtable();
                    respdata["success"] = "TRUE";
                    resp.Value = respdata;
                }
            }

            return resp;
        }
        
        // Grid Request Processing
        /// <summary>
        /// Ooops, our Agent must be dead if we're getting this request!
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse LogOffUser(XmlRpcRequest request)
        {
            m_log.Debug("[CONNECTION DEBUGGING]: LogOff User Called");
            
            Hashtable requestData = (Hashtable)request.Params[0];
            string message = (string)requestData["message"];
            UUID agentID = UUID.Zero;
            UUID RegionSecret = UUID.Zero;
            UUID.TryParse((string)requestData["agent_id"], out agentID);
            UUID.TryParse((string)requestData["region_secret"], out RegionSecret);

            ulong regionHandle = Convert.ToUInt64((string)requestData["regionhandle"]);

            m_localBackend.TriggerLogOffUser(regionHandle, agentID, RegionSecret,message);

            return new XmlRpcResponse();
        }

        #region m_interRegion Comms

        /// <summary>
        /// Start listening for .net remoting calls from other regions.
        /// </summary>
        private void StartRemoting()
        {
            TcpChannel ch;
            try
            {
                ch = new TcpChannel((int)NetworkServersInfo.RemotingListenerPort);
                ChannelServices.RegisterChannel(ch, false); // Disabled security as Mono doesn't support this.
            }
            catch (Exception ex)
            {
                m_log.Error("[OGS1 GRID SERVICES]: Exception while attempting to listen on TCP port " + (int)NetworkServersInfo.RemotingListenerPort + ".");
                throw (ex);
            }

            WellKnownServiceTypeEntry wellType =
                new WellKnownServiceTypeEntry(typeof (OGS1InterRegionRemoting), "InterRegions",
                                              WellKnownObjectMode.Singleton);
            RemotingConfiguration.RegisterWellKnownServiceType(wellType);
            InterRegionSingleton.Instance.OnArrival += TriggerExpectAvatarCrossing;
            InterRegionSingleton.Instance.OnChildAgent += IncomingChildAgent;
            InterRegionSingleton.Instance.OnPrimGroupArrival += IncomingPrim;
            InterRegionSingleton.Instance.OnPrimGroupNear += TriggerExpectPrimCrossing;
            InterRegionSingleton.Instance.OnRegionUp += TriggerRegionUp;
            InterRegionSingleton.Instance.OnChildAgentUpdate += TriggerChildAgentUpdate;
            InterRegionSingleton.Instance.OnTellRegionToCloseChildConnection += TriggerTellRegionToCloseChildConnection;
        }

        #region Methods called by regions in this instance

        public bool ChildAgentUpdate(ulong regionHandle, ChildAgentDataUpdate cAgentData)
        {
            int failures = 0;
            lock (m_deadRegionCache)
            {
                if (m_deadRegionCache.ContainsKey(regionHandle))
                {
                    failures = m_deadRegionCache[regionHandle];
                }
            }
            if (failures <= 3)
            {
                RegionInfo regInfo = null;
                try
                {
                    if (m_localBackend.ChildAgentUpdate(regionHandle, cAgentData))
                    {
                        return true;
                    }

                    regInfo = RequestNeighbourInfo(regionHandle);
                    if (regInfo != null)
                    {
                        //don't want to be creating a new link to the remote instance every time like we are here
                        bool retValue = false;


                        OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting)Activator.GetObject(
                            typeof(OGS1InterRegionRemoting),
                            "tcp://" + regInfo.RemotingAddress +
                            ":" + regInfo.RemotingPort +
                            "/InterRegions");

                        if (remObject != null)
                        {
                            retValue = remObject.ChildAgentUpdate(regionHandle, cAgentData);
                        }
                        else
                        {
                            m_log.Warn("[OGS1 GRID SERVICES]: remoting object not found");
                        }
                        remObject = null;
//                         m_log.Info("[INTER]: " +
//                                    gdebugRegionName +
//                                    ": OGS1 tried to Update Child Agent data on outside region and got " +
//                                    retValue.ToString());

                        return retValue;
                    }
                    NoteDeadRegion(regionHandle);

                    return false;
                }
                catch (RemotingException e)
                {
                    NoteDeadRegion(regionHandle);

                    m_log.WarnFormat(
                        "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                        regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                    return false;
                }
                catch (SocketException e)
                {
                    NoteDeadRegion(regionHandle);

                    m_log.WarnFormat(
                        "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                        regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                    return false;
                }
                catch (InvalidCredentialException e)
                {
                    NoteDeadRegion(regionHandle);

                    m_log.WarnFormat(
                        "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                        regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                    return false;
                }
                catch (AuthenticationException e)
                {
                    NoteDeadRegion(regionHandle);

                    m_log.WarnFormat(
                        "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                        regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                    return false;
                }
                catch (Exception e)
                {
                    NoteDeadRegion(regionHandle);

                    m_log.WarnFormat("[OGS1 GRID SERVICES]: Unable to connect to adjacent region: {0} {1},{2}",
                                     regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                    return false;
                }
            }
            else
            {
                //m_log.Info("[INTERREGION]: Skipped Sending Child Update to a region because it failed too many times:" + regionHandle.ToString());
                return false;
            }
        }

        /// <summary>
        /// Inform a region that a child agent will be on the way from a client.
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentData"></param>
        /// <returns></returns>
        public bool InformRegionOfChildAgent(ulong regionHandle, AgentCircuitData agentData)
        {
            RegionInfo regInfo = null;
            try
            {
                if (m_localBackend.InformRegionOfChildAgent(regionHandle, agentData))
                {
                    return true;
                }

                regInfo = RequestNeighbourInfo(regionHandle);
                if (regInfo != null)
                {
                    //don't want to be creating a new link to the remote instance every time like we are here
                    bool retValue = false;

                    OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting)Activator.GetObject(
                        typeof(OGS1InterRegionRemoting),
                        "tcp://" + regInfo.RemotingAddress +
                        ":" + regInfo.RemotingPort +
                        "/InterRegions");

                    if (remObject != null)
                    {
                        retValue = remObject.InformRegionOfChildAgent(regionHandle, new sAgentCircuitData(agentData));
                    }
                    else
                    {
                        m_log.Warn("[OGS1 GRID SERVICES]: remoting object not found");
                    }
                    remObject = null;
                    m_log.Info("[OGS1 GRID SERVICES]: " +
                           m_localBackend._gdebugRegionName + ": OGS1 tried to InformRegionOfChildAgent for " +
                           agentData.firstname + " " + agentData.lastname + " and got " +
                           retValue.ToString());

                    return retValue;
                }
                NoteDeadRegion(regionHandle);
                return false;
            }
            catch (RemotingException e)
            {
                NoteDeadRegion(regionHandle);

                m_log.WarnFormat(
                    "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                    regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
            catch (SocketException e)
            {
                NoteDeadRegion(regionHandle);

                m_log.WarnFormat(
                    "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                    regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
            catch (InvalidCredentialException e)
            {
                NoteDeadRegion(regionHandle);

                m_log.WarnFormat(
                    "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                    regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
            catch (AuthenticationException e)
            {
                NoteDeadRegion(regionHandle);

                m_log.WarnFormat(
                    "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                    regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
            catch (Exception e)
            {
                NoteDeadRegion(regionHandle);

                if (regInfo != null)
                {
                    m_log.WarnFormat(
                        "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                        regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                }
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
        }

        // UGLY!
        public bool RegionUp(SerializableRegionInfo region, ulong regionhandle)
        {
            SerializableRegionInfo regInfo = null;
            try
            {
                // You may ask why this is in here...
                // The region asking the grid services about itself..
                // And, surprisingly, the reason is..  it doesn't know
                // it's own remoting port!  How special.
                RegionUpData regiondata = new RegionUpData(region.RegionLocX, region.RegionLocY, region.ExternalHostName, region.InternalEndPoint.Port);

                region = new SerializableRegionInfo(RequestNeighbourInfo(region.RegionHandle));
                region.RemotingAddress = region.ExternalHostName;
                region.RemotingPort = NetworkServersInfo.RemotingListenerPort;
                region.HttpPort = serversInfo.HttpListenerPort;

                if (m_localBackend.RegionUp(region, regionhandle))
                {
                    return true;
                }

                regInfo = new SerializableRegionInfo(RequestNeighbourInfo(regionhandle));
                if (regInfo != null)
                {
                    // If we're not trying to remote to ourselves.
                    if (regInfo.RemotingAddress != region.RemotingAddress && region.RemotingAddress != null)
                    {
                        //don't want to be creating a new link to the remote instance every time like we are here
                        bool retValue = false;

                        OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting) Activator.GetObject(
                            typeof(OGS1InterRegionRemoting),
                            "tcp://" +
                            regInfo.RemotingAddress +
                            ":" + regInfo.RemotingPort +
                            "/InterRegions");

                        if (remObject != null)
                        {
                            retValue = remObject.RegionUp(regiondata, regionhandle);
                        }
                        else
                        {
                            m_log.Warn("[OGS1 GRID SERVICES]: remoting object not found");
                        }
                        remObject = null;
                        
                        m_log.Info(
                            "[INTER]: " + m_localBackend._gdebugRegionName + ": OGS1 tried to inform region I'm up");

                        return retValue;
                    }
                    else
                    {
                        // We're trying to inform ourselves via remoting.
                        // This is here because we're looping over the listeners before we get here.
                        // Odd but it should work.
                        return true;
                    }
                }

                return false;
            }
            catch (RemotingException e)
            {
                m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY +
                           " - Is this neighbor up?");
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (SocketException e)
            {
                m_log.Warn("[OGS1 GRID SERVICES]: Socket Error: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY +
                           " - Is this neighbor up?");
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (InvalidCredentialException e)
            {
                m_log.Warn("[OGS1 GRID SERVICES]: Invalid Credentials: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (AuthenticationException e)
            {
                m_log.Warn("[OGS1 GRID SERVICES]: Authentication exception: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (Exception e)
            {
                // This line errors with a Null Reference Exception..    Why?  @.@
                //m_log.Warn("Unknown exception: Unable to connect to adjacent region using tcp://" + regInfo.RemotingAddress +
                // ":" + regInfo.RemotingPort +
                //"/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY + " - This is likely caused by an incompatibility in the protocol between this sim and that one");
                m_log.Debug(e.ToString());
                return false;
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentData"></param>
        /// <returns></returns>
        public bool InformRegionOfPrimCrossing(ulong regionHandle, UUID primID, string objData, int XMLMethod)
        {
            int failures = 0;
            lock (m_deadRegionCache)
            {
                if (m_deadRegionCache.ContainsKey(regionHandle))
                {
                    failures = m_deadRegionCache[regionHandle];
                }
            }
            if (failures <= 1)
            {
                RegionInfo regInfo = null;
                try
                {
                    if (m_localBackend.InformRegionOfPrimCrossing(regionHandle, primID, objData, XMLMethod))
                    {
                        return true;
                    }

                    regInfo = RequestNeighbourInfo(regionHandle);
                    if (regInfo != null)
                    {
                        //don't want to be creating a new link to the remote instance every time like we are here
                        bool retValue = false;

                        OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting)Activator.GetObject(
                            typeof(OGS1InterRegionRemoting),
                            "tcp://" + regInfo.RemotingAddress +
                            ":" + regInfo.RemotingPort +
                            "/InterRegions");

                        if (remObject != null)
                        {
                            retValue = remObject.InformRegionOfPrimCrossing(regionHandle, primID.Guid, objData, XMLMethod);
                        }
                        else
                        {
                            m_log.Warn("[OGS1 GRID SERVICES]: Remoting object not found");
                        }
                        remObject = null;

                        return retValue;
                    }
                    NoteDeadRegion(regionHandle);
                    return false;
                }
                catch (RemotingException e)
                {
                    NoteDeadRegion(regionHandle);
                    m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: " + regionHandle);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                    return false;
                }
                catch (SocketException e)
                {
                    NoteDeadRegion(regionHandle);
                    m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: " + regionHandle);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                    return false;
                }
                catch (InvalidCredentialException e)
                {
                    NoteDeadRegion(regionHandle);
                    m_log.Warn("[OGS1 GRID SERVICES]: Invalid Credential Exception: Invalid Credentials : " + regionHandle);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                    return false;
                }
                catch (AuthenticationException e)
                {
                    NoteDeadRegion(regionHandle);
                    m_log.Warn("[OGS1 GRID SERVICES]: Authentication exception: Unable to connect to adjacent region: " + regionHandle);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                    return false;
                }
                catch (Exception e)
                {
                    NoteDeadRegion(regionHandle);
                    m_log.Warn("[OGS1 GRID SERVICES]: Unknown exception: Unable to connect to adjacent region: " + regionHandle);
                    m_log.DebugFormat("[OGS1 GRID SERVICES]: {0}", e);
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentID"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public bool ExpectAvatarCrossing(ulong regionHandle, UUID agentID, Vector3 position, bool isFlying)
        {
            RegionInfo[] regions = m_regionsOnInstance.ToArray();
            bool banned = false;

            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i] != null)
                {
                    if (regions[i].RegionHandle == regionHandle)
                    {
                        if (regions[i].EstateSettings.IsBanned(agentID))
                        {
                            banned = true;
                            break;
                        }
                    }
                }
            }

            if (banned)
                return false;

            RegionInfo regInfo = null;
            try
            {
                if (m_localBackend.TriggerExpectAvatarCrossing(regionHandle, agentID, position, isFlying))
                {
                    return true;
                }

                regInfo = RequestNeighbourInfo(regionHandle);
                if (regInfo != null)
                {
                    bool retValue = false;
                    OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting) Activator.GetObject(
                        typeof (OGS1InterRegionRemoting),
                        "tcp://" + regInfo.RemotingAddress +
                        ":" + regInfo.RemotingPort +
                        "/InterRegions");

                    if (remObject != null)
                    {
                        retValue =
                            remObject.ExpectAvatarCrossing(regionHandle, agentID.Guid, new sLLVector3(position),
                                                           isFlying);
                    }
                    else
                    {
                        m_log.Warn("[OGS1 GRID SERVICES]: Remoting object not found");
                    }
                    remObject = null;

                    return retValue;
                }
                //TODO need to see if we know about where this region is and use .net remoting
                // to inform it.
                NoteDeadRegion(regionHandle);
                return false;
            }
            catch (RemotingException e)
            {
                NoteDeadRegion(regionHandle);

                m_log.WarnFormat(
                    "[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: {0} {1},{2}",
                    regInfo.RegionName, regInfo.RegionLocX, regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);

                return false;
            }
            catch
            {
                NoteDeadRegion(regionHandle);
                return false;
            }
        }

        public bool ExpectPrimCrossing(ulong regionHandle, UUID agentID, Vector3 position, bool isPhysical)
        {
            RegionInfo regInfo = null;
            try
            {
                if (m_localBackend.TriggerExpectPrimCrossing(regionHandle, agentID, position, isPhysical))
                {
                    return true;
                }

                regInfo = RequestNeighbourInfo(regionHandle);
                if (regInfo != null)
                {
                    bool retValue = false;
                    OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting) Activator.GetObject(
                        typeof (OGS1InterRegionRemoting),
                        "tcp://" + regInfo.RemotingAddress +
                        ":" + regInfo.RemotingPort +
                        "/InterRegions");

                    if (remObject != null)
                    {
                        retValue =
                            remObject.ExpectAvatarCrossing(regionHandle, agentID.Guid, new sLLVector3(position),
                                                           isPhysical);
                    }
                    else
                    {
                        m_log.Warn("[OGS1 GRID SERVICES]: Remoting object not found");
                    }
                    remObject = null;

                    return retValue;
                }
                //TODO need to see if we know about where this region is and use .net remoting
                // to inform it.
                NoteDeadRegion(regionHandle);
                return false;
            }
            catch (RemotingException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: " + regionHandle);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (SocketException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region: " + regionHandle);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (InvalidCredentialException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Invalid Credential Exception: Invalid Credentials : " + regionHandle);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (AuthenticationException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Authentication exception: Unable to connect to adjacent region: " + regionHandle);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (Exception e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Unknown exception: Unable to connect to adjacent region: " + regionHandle);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0}", e);
                return false;
            }
        }

        public bool TellRegionToCloseChildConnection(ulong regionHandle, UUID agentID)
        {
            RegionInfo regInfo = null;
            try
            {
                if (m_localBackend.TriggerTellRegionToCloseChildConnection(regionHandle, agentID))
                {
                    return true;
                }

                regInfo = RequestNeighbourInfo(regionHandle);
                if (regInfo != null)
                {
                    // bool retValue = false;
                    OGS1InterRegionRemoting remObject = (OGS1InterRegionRemoting)Activator.GetObject(
                        typeof(OGS1InterRegionRemoting),
                        "tcp://" + regInfo.RemotingAddress +
                        ":" + regInfo.RemotingPort +
                        "/InterRegions");

                    if (remObject != null)
                    {
                        // retValue =
                        remObject.TellRegionToCloseChildConnection(regionHandle, agentID.Guid);
                    }
                    else
                    {
                        m_log.Warn("[OGS1 GRID SERVICES]: Remoting object not found");
                    }
                    remObject = null;

                    return true;
                }
                //TODO need to see if we know about where this region is and use .net remoting
                // to inform it.
                NoteDeadRegion(regionHandle);
                return false;
            }
            catch (RemotingException)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Remoting Error: Unable to connect to adjacent region to tell it to close child agents: " + regInfo.RegionName +
                           " " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                //m_log.Debug(e.ToString());
                return false;
            }
            catch (SocketException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Socket Error: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY +
                           " - Is this neighbor up?");
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (InvalidCredentialException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Invalid Credentials: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (AuthenticationException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: Authentication exception: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (WebException e)
            {
                NoteDeadRegion(regionHandle);
                m_log.Warn("[OGS1 GRID SERVICES]: WebException exception: Unable to connect to adjacent region using tcp://" +
                           regInfo.RemotingAddress +
                           ":" + regInfo.RemotingPort +
                           "/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY);
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0} {1}", e.Source, e.Message);
                return false;
            }
            catch (Exception e)
            {
                NoteDeadRegion(regionHandle);
                // This line errors with a Null Reference Exception..    Why?  @.@
                //m_log.Warn("Unknown exception: Unable to connect to adjacent region using tcp://" + regInfo.RemotingAddress +
                // ":" + regInfo.RemotingPort +
                //"/InterRegions - @ " + regInfo.RegionLocX + "," + regInfo.RegionLocY + " - This is likely caused by an incompatibility in the protocol between this sim and that one");
                m_log.DebugFormat("[OGS1 GRID SERVICES]: {0}", e);
                return false;
            }
        }

        public bool AcknowledgeAgentCrossed(ulong regionHandle, UUID agentId)
        {
            return m_localBackend.AcknowledgeAgentCrossed(regionHandle, agentId);
        }

        public bool AcknowledgePrimCrossed(ulong regionHandle, UUID primId)
        {
            return m_localBackend.AcknowledgePrimCrossed(regionHandle, primId);
        }

        #endregion

        #region Methods triggered by calls from external instances

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentData"></param>
        /// <returns></returns>
        public bool IncomingChildAgent(ulong regionHandle, AgentCircuitData agentData)
        {
            //m_log.Info("[INTER]: " + gdebugRegionName + ": Incoming OGS1 Agent " + agentData.firstname + " " + agentData.lastname);

            return m_localBackend.IncomingChildAgent(regionHandle, agentData);
        }

        public bool TriggerRegionUp(RegionUpData regionData, ulong regionhandle)
        {
            m_log.Info(
               "[OGS1 GRID SERVICES]: " +
               m_localBackend._gdebugRegionName + "Incoming OGS1 RegionUpReport:  " + "(" + regionData.X +
               "," + regionData.Y + "). Giving this region a fresh set of 'dead' tries");
            
            RegionInfo nRegionInfo = new RegionInfo();
            nRegionInfo.SetEndPoint("127.0.0.1", regionData.PORT);
            nRegionInfo.ExternalHostName = regionData.IPADDR;
            nRegionInfo.RegionLocX = regionData.X;
            nRegionInfo.RegionLocY = regionData.Y;

            lock (m_deadRegionCache)
            {
                if (m_deadRegionCache.ContainsKey(nRegionInfo.RegionHandle))
                {
                    m_deadRegionCache.Remove(nRegionInfo.RegionHandle);
                }
            }

            return m_localBackend.TriggerRegionUp(nRegionInfo, regionhandle);
        }

        public bool TriggerChildAgentUpdate(ulong regionHandle, ChildAgentDataUpdate cAgentData)
        {
            //m_log.Info("[INTER]: Incoming OGS1 Child Agent Data Update");

            return m_localBackend.TriggerChildAgentUpdate(regionHandle, cAgentData);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentData"></param>
        /// <returns></returns>
        public bool IncomingPrim(ulong regionHandle, UUID primID, string objData, int XMLMethod)
        {
            m_localBackend.TriggerExpectPrim(regionHandle, primID, objData, XMLMethod);
            
            return true;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentID"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public bool TriggerExpectAvatarCrossing(ulong regionHandle, UUID agentID, Vector3 position, bool isFlying)
        {
            return m_localBackend.TriggerExpectAvatarCrossing(regionHandle, agentID, position, isFlying);
        }

        public bool TriggerExpectPrimCrossing(ulong regionHandle, UUID agentID, Vector3 position, bool isPhysical)
        {
            return m_localBackend.TriggerExpectPrimCrossing(regionHandle, agentID, position, isPhysical);
        }

        public bool TriggerTellRegionToCloseChildConnection(ulong regionHandle, UUID agentID)
        {
            return m_localBackend.TriggerTellRegionToCloseChildConnection(regionHandle, agentID);
        }

        #endregion

        #endregion

        int timeOut = 10; //10 seconds

        /// <summary>
        /// Check that a region is available for TCP comms.  This is necessary for .NET remoting between regions.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="retry"></param>
        /// <returns></returns>
        public bool CheckRegion(string address, uint port, bool retry)
        {
            bool available = false;
            bool timed_out = true;

            IPAddress ia;
            IPAddress.TryParse(address, out ia);
            IPEndPoint m_EndPoint = new IPEndPoint(ia, (int)port);

            AsyncCallback callback = delegate(IAsyncResult iar)
            {
                Socket s = (Socket)iar.AsyncState;
                try
                {
                    s.EndConnect(iar);
                    available = true;
                    timed_out = false;
                }
                catch (Exception e)
                {
                    m_log.DebugFormat(
                        "[OGS1 GRID SERVICES]: Callback EndConnect exception: {0}:{1}", e.Message, e.StackTrace);
                }

                s.Close();
            };

            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = socket.BeginConnect(m_EndPoint, callback, socket);
                ar.AsyncWaitHandle.WaitOne(timeOut * 1000, false);
            }
            catch (Exception e)
            {
                m_log.DebugFormat(
                    "[OGS1 GRID SERVICES]: CheckRegion Socket Setup exception: {0}:{1}", e.Message, e.StackTrace);

                return false;
            }

            if (timed_out)
            {
                m_log.DebugFormat(
                    "[OGS1 GRID SERVICES]: socket [{0}] timed out ({1}) waiting to obtain a connection.",
                    m_EndPoint, timeOut * 1000);

                if (retry)
                {
                    return CheckRegion(address, port, false);
                }
            }

            return available;
        }

        public bool CheckRegion(string address, uint port)
        {
            return CheckRegion(address, port, true);
        }

        public void NoteDeadRegion(ulong regionhandle)
        {
            lock (m_deadRegionCache)
            {
                if (m_deadRegionCache.ContainsKey(regionhandle))
                {
                    m_deadRegionCache[regionhandle] = m_deadRegionCache[regionhandle] + 1;
                }
                else
                {
                    m_deadRegionCache.Add(regionhandle, 1);
                }
            }
        }

        public LandData RequestLandData (ulong regionHandle, uint x, uint y)
        {
            m_log.DebugFormat("[OGS1 GRID SERVICES] requests land data in {0}, at {1}, {2}",
                              regionHandle, x, y);
            LandData landData = m_localBackend.RequestLandData(regionHandle, x, y);
            if (landData == null)
            {
                Hashtable hash = new Hashtable();
                hash["region_handle"] = regionHandle.ToString();
                hash["x"] = x.ToString();
                hash["y"] = y.ToString();

                IList paramList = new ArrayList();
                paramList.Add(hash);

                try
                {
                    // this might be cached, as we probably requested it just a moment ago...
                    RegionInfo info = RequestNeighbourInfo(regionHandle);
                    if (info != null) // just to be sure
                    {
                        XmlRpcRequest request = new XmlRpcRequest("land_data", paramList);
                        string uri = "http://" + info.ExternalEndPoint.Address + ":" + info.HttpPort + "/";
                        XmlRpcResponse response = request.Send(uri, 10000);
                        if (response.IsFault)
                        {
                            m_log.ErrorFormat("[OGS1 GRID SERVICES] remote call returned an error: {0}", response.FaultString);
                        }
                        else
                        {
                            hash = (Hashtable)response.Value;
                            try
                            {
                                landData = new LandData();
                                landData.AABBMax = Vector3.Parse((string)hash["AABBMax"]);
                                landData.AABBMin = Vector3.Parse((string)hash["AABBMin"]);
                                landData.Area = Convert.ToInt32(hash["Area"]);
                                landData.AuctionID = Convert.ToUInt32(hash["AuctionID"]);
                                landData.Description = (string)hash["Description"];
                                landData.Flags = Convert.ToUInt32(hash["Flags"]);
                                landData.GlobalID = new UUID((string)hash["GlobalID"]);
                                landData.Name = (string)hash["Name"];
                                landData.OwnerID = new UUID((string)hash["OwnerID"]);
                                landData.SalePrice = Convert.ToInt32(hash["SalePrice"]);
                                landData.SnapshotID = new UUID((string)hash["SnapshotID"]);
                                landData.UserLocation = Vector3.Parse((string)hash["UserLocation"]);
                                m_log.DebugFormat("[OGS1 GRID SERVICES] Got land data for parcel {0}", landData.Name);
                            }
                            catch (Exception e)
                            {
                                m_log.Error("[OGS1 GRID SERVICES] Got exception while parsing land-data:", e);
                            }
                        }
                    }
                    else m_log.WarnFormat("[OGS1 GRID SERVICES] Couldn't find region with handle {0}", regionHandle);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[OGS1 GRID SERVICES] Couldn't contact region {0}: {1}", regionHandle, e);
                }
            }
            return landData;
        }

        // Grid Request Processing
        /// <summary>
        /// Someone asked us about parcel-information
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public XmlRpcResponse LandData(XmlRpcRequest request)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            ulong regionHandle = Convert.ToUInt64(requestData["region_handle"]);
            uint x = Convert.ToUInt32(requestData["x"]);
            uint y = Convert.ToUInt32(requestData["y"]);
            m_log.DebugFormat("[OGS1 GRID SERVICES]: Got XML reqeuest for land data at {0}, {1} in region {2}", x, y, regionHandle);

            LandData landData = m_localBackend.RequestLandData(regionHandle, x, y);
            Hashtable hash = new Hashtable();
            if (landData != null)
            {
                // for now, only push out the data we need for answering a ParcelInfoReqeust
                hash["AABBMax"] = landData.AABBMax.ToString();
                hash["AABBMin"] = landData.AABBMin.ToString();
                hash["Area"] = landData.Area.ToString();
                hash["AuctionID"] = landData.AuctionID.ToString();
                hash["Description"] = landData.Description;
                hash["Flags"] = landData.Flags.ToString();
                hash["GlobalID"] = landData.GlobalID.ToString();
                hash["Name"] = landData.Name;
                hash["OwnerID"] = landData.OwnerID.ToString();
                hash["SalePrice"] = landData.SalePrice.ToString();
                hash["SnapshotID"] = landData.SnapshotID.ToString();
                hash["UserLocation"] = landData.UserLocation.ToString();
            }

            XmlRpcResponse response = new XmlRpcResponse();
            response.Value = hash;
            return response;
        }

        public List<RegionInfo> RequestNamedRegions (string name, int maxNumber)
        {
            // no asking of the local backend first, here, as we have to ask the gridserver anyway.
            Hashtable hash = new Hashtable();
            hash["name"] = name;
            hash["maxNumber"] = maxNumber.ToString();

            IList paramList = new ArrayList();
            paramList.Add(hash);

            Hashtable result = XmlRpcSearchForRegionByName(paramList);
            if (result == null) return null;

            uint numberFound = Convert.ToUInt32(result["numFound"]);
            List<RegionInfo> infos = new List<RegionInfo>();
            for (int i = 0; i < numberFound; ++i)
            {
                string prefix = "region" + i + ".";
                RegionInfo info = buildRegionInfo(result, prefix);
                infos.Add(info);
            }
            return infos;
        }

        private RegionInfo buildRegionInfo(Hashtable responseData, string prefix)
        {
            uint regX = Convert.ToUInt32((string) responseData[prefix + "region_locx"]);
            uint regY = Convert.ToUInt32((string) responseData[prefix + "region_locy"]);
            string internalIpStr = (string) responseData[prefix + "sim_ip"];
            uint port = Convert.ToUInt32(responseData[prefix + "sim_port"]);

            IPEndPoint neighbourInternalEndPoint = new IPEndPoint(Util.GetHostFromDNS(internalIpStr), (int) port);

            RegionInfo regionInfo = new RegionInfo(regX, regY, neighbourInternalEndPoint, internalIpStr);
            regionInfo.RemotingPort = Convert.ToUInt32((string) responseData[prefix + "remoting_port"]);
            regionInfo.RemotingAddress = internalIpStr;

            if (responseData.ContainsKey(prefix + "http_port"))
            {
                regionInfo.HttpPort = Convert.ToUInt32((string) responseData[prefix + "http_port"]);
            }

            regionInfo.RegionID = new UUID((string) responseData[prefix + "region_UUID"]);
            regionInfo.RegionName = (string) responseData[prefix + "region_name"];

            regionInfo.RegionSettings.TerrainImageID = new UUID((string) responseData[prefix + "map_UUID"]);
            return regionInfo;
        }

        private Hashtable XmlRpcSearchForRegionByName(IList parameters)
        {
            try
            {
                XmlRpcRequest request = new XmlRpcRequest("search_for_region_by_name", parameters);
                XmlRpcResponse resp = request.Send(serversInfo.GridURL, 10000);
                Hashtable respData = (Hashtable) resp.Value;
                if (respData != null && respData.Contains("faultCode"))
                {
                    m_log.WarnFormat("[OGS1 GRID SERVICES]: Got an error while contacting GridServer: {0}", respData["faultString"]);
                    return null;
                }

                return respData;
            }
            catch (Exception e)
            {
                m_log.Error("[OGS1 GRID SERVICES]: MapBlockQuery XMLRPC failure: ", e);
                return null;
            }
        }

        public List<UUID> InformFriendsInOtherRegion(UUID agentId, ulong destRegionHandle, List<UUID> friends, bool online)
        {
            List<UUID> tpdAway = new List<UUID>();

            // destRegionHandle is a region on another server
            RegionInfo info = RequestNeighbourInfo(destRegionHandle);
            if (info != null)
            {
                string httpServer = "http://" + info.ExternalEndPoint.Address + ":" + info.HttpPort + "/presence_update_bulk";

                Hashtable reqParams = new Hashtable();
                reqParams["agentID"] = agentId.ToString();
                reqParams["agentOnline"] = online;
                int count = 0;
                foreach (UUID uuid in friends)
                {
                    reqParams["friendID_" + count++] = uuid.ToString();
                }
                reqParams["friendCount"] = count;

                IList parameters = new ArrayList();
                parameters.Add(reqParams);
                try
                {
                    XmlRpcRequest request = new XmlRpcRequest("presence_update_bulk", parameters);
                    XmlRpcResponse response = request.Send(httpServer, 5000);
                    Hashtable respData = (Hashtable)response.Value;

                    count = (int)respData["friendCount"];
                    for (int i = 0; i < count; ++i)
                    {
                        UUID uuid;
                        if (UUID.TryParse((string)respData["friendID_" + i], out uuid)) tpdAway.Add(uuid);
                    }
                }
                catch (Exception e)
                {
                    m_log.Error("[OGS1 GRID SERVICES]: InformFriendsInOtherRegion XMLRPC failure: ", e);
                }
            }
            else m_log.WarnFormat("[OGS1 GRID SERVICES]: Couldn't find region {0}???", destRegionHandle);

            return tpdAway;
        }

        public bool TriggerTerminateFriend(ulong destRegionHandle, UUID agentID, UUID exFriendID)
        {
            // destRegionHandle is a region on another server
            RegionInfo info = RequestNeighbourInfo(destRegionHandle);
            if (info == null)
            {
                m_log.WarnFormat("[OGS1 GRID SERVICES]: Couldn't find region {0}", destRegionHandle);
                return false; // region not found???
            }

            string httpServer = "http://" + info.ExternalEndPoint.Address + ":" + info.HttpPort + "/presence_update_bulk";

            Hashtable reqParams = new Hashtable();
            reqParams["agentID"] = agentID.ToString();
            reqParams["friendID"] = exFriendID.ToString();

            IList parameters = new ArrayList();
            parameters.Add(reqParams);
            try
            {
                XmlRpcRequest request = new XmlRpcRequest("terminate_friend", parameters);
                XmlRpcResponse response = request.Send(httpServer, 5000);
                Hashtable respData = (Hashtable)response.Value;

                return (bool)respData["success"];
            }
            catch (Exception e)
            {
                m_log.Error("[OGS1 GRID SERVICES]: InformFriendsInOtherRegion XMLRPC failure: ", e);
                return false;
            }
        }
    }
}
