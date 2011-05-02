﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using OpenMetaverse;
using Nini.Config;
using log4net;

using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Region.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    public delegate void UpLoadedAsset(
    string assetName, string description, UUID assetID, UUID inventoryItem, UUID parentFolder,
    byte[] data, string inventoryType, string assetType);

    public delegate void UploadedBakedTexture(UUID assetID, byte[] data);

    public delegate UUID UpdateItem(UUID itemID, byte[] data);

    public delegate void UpdateTaskScript(UUID itemID, UUID primID, bool isScriptRunning, byte[] data, ref ArrayList errors);

    public delegate void NewInventoryItem(UUID userID, InventoryItemBase item);

    public delegate void NewAsset(AssetBase asset);

    public delegate UUID ItemUpdatedCallback(UUID userID, UUID itemID, byte[] data);

    public delegate ArrayList TaskScriptUpdatedCallback(UUID userID, UUID itemID, UUID primID,
                                                   bool isScriptRunning, byte[] data);

    public delegate InventoryCollection FetchInventoryDescendentsCAPS(UUID agentID, UUID folderID, UUID ownerID,
                                                                          bool fetchFolders, bool fetchItems, int sortOrder, out int version);

    /// <summary>
    /// XXX Probably not a particularly nice way of allow us to get the scene presence from the scene (chiefly so that
    /// we can popup a message on the user's client if the inventory service has permanently failed).  But I didn't want
    /// to just pass the whole Scene into CAPS.
    /// </summary>
    public delegate IClientAPI GetClientDelegate(UUID agentID);

    public class BunchOfCaps
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_Scene;
        private Caps m_HostCapsObj;

        private static readonly string m_requestPath = "0000/";
        // private static readonly string m_mapLayerPath = "0001/";
        private static readonly string m_newInventory = "0002/";
        //private static readonly string m_requestTexture = "0003/";
        private static readonly string m_notecardUpdatePath = "0004/";
        private static readonly string m_notecardTaskUpdatePath = "0005/";
        //        private static readonly string m_fetchInventoryPath = "0006/";
        // private static readonly string m_remoteParcelRequestPath = "0009/";// This is in the LandManagementModule.
        private static readonly string m_uploadBakedTexturePath = "0010/";// This is in the LandManagementModule.


        // These are callbacks which will be setup by the scene so that we can update scene data when we
        // receive capability calls
        public NewInventoryItem AddNewInventoryItem = null;
        public NewAsset AddNewAsset = null;
        public ItemUpdatedCallback ItemUpdatedCall = null;
        public TaskScriptUpdatedCallback TaskScriptUpdatedCall = null;
        public FetchInventoryDescendentsCAPS CAPSFetchInventoryDescendents = null;
        public GetClientDelegate GetClient = null;

        private bool m_persistBakedTextures = false;
        private IAssetService m_assetService;
        private bool m_dumpAssetsToFile;
        private string m_regionName;
        private object m_fetchLock = new Object();

        public BunchOfCaps(Scene scene, Caps caps)
        {
            m_Scene = scene;
            m_HostCapsObj = caps;
            IConfigSource config = m_Scene.Config;
            if (config != null)
            {
                IConfig sconfig = config.Configs["Startup"];
                if (sconfig != null)
                    m_persistBakedTextures = sconfig.GetBoolean("PersistBakedTextures", m_persistBakedTextures);
            }

            m_assetService = m_Scene.AssetService;
            m_regionName = m_Scene.RegionInfo.RegionName;

            RegisterHandlers();

            AddNewInventoryItem = m_Scene.AddUploadedInventoryItem;
            ItemUpdatedCall = m_Scene.CapsUpdateInventoryItemAsset;
            TaskScriptUpdatedCall = m_Scene.CapsUpdateTaskInventoryScriptAsset;
            CAPSFetchInventoryDescendents = m_Scene.HandleFetchInventoryDescendentsCAPS;
            GetClient = m_Scene.SceneContents.GetControllingClient;

        }

        /// <summary>
        /// Register a bunch of CAPS http service handlers
        /// </summary>
        public void RegisterHandlers()
        {
            m_HostCapsObj.DeregisterHandlers();

            string capsBase = "/CAPS/" + m_HostCapsObj.CapsObjectPath;

            RegisterRegionServiceHandlers(capsBase);
            RegisterInventoryServiceHandlers(capsBase);
        }

        public void RegisterRegionServiceHandlers(string capsBase)
        {
            try
            {
                // the root of all evil
                m_HostCapsObj.RegisterHandler("SEED", new RestStreamHandler("POST", capsBase + m_requestPath, SeedCapRequest));
                m_log.DebugFormat(
                    "[CAPS]: Registered seed capability {0} for {1}", capsBase + m_requestPath, m_HostCapsObj.AgentID);

                //m_capsHandlers["MapLayer"] =
                //    new LLSDStreamhandler<OSDMapRequest, OSDMapLayerResponse>("POST",
                //                                                                capsBase + m_mapLayerPath,
                //                                                                GetMapLayer);
                IRequestHandler req = new RestStreamHandler("POST", capsBase + m_notecardTaskUpdatePath, ScriptTaskInventory);
                m_HostCapsObj.RegisterHandler("UpdateScriptTaskInventory", req);
                m_HostCapsObj.RegisterHandler("UpdateScriptTask", req);
                m_HostCapsObj.RegisterHandler("UploadBakedTexture", new RestStreamHandler("POST", capsBase + m_uploadBakedTexturePath, UploadBakedTexture));

            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: " + e.ToString());
            }
        }

        public void RegisterInventoryServiceHandlers(string capsBase)
        {
            try
            {
                // I don't think this one works...
                m_HostCapsObj.RegisterHandler("NewFileAgentInventory", new LLSDStreamhandler<LLSDAssetUploadRequest, LLSDAssetUploadResponse>("POST",
                                                                                           capsBase + m_newInventory,
                                                                                           NewAgentInventoryRequest));
                IRequestHandler req = new RestStreamHandler("POST", capsBase + m_notecardUpdatePath, NoteCardAgentInventory);
                m_HostCapsObj.RegisterHandler("UpdateNotecardAgentInventory", req);
                m_HostCapsObj.RegisterHandler("UpdateScriptAgentInventory", req);
                m_HostCapsObj.RegisterHandler("UpdateScriptAgent", req);
             
                // As of RC 1.22.9 of the Linden client this is
                // supported

                //m_capsHandlers["WebFetchInventoryDescendents"] =new RestStreamHandler("POST", capsBase + m_fetchInventoryPath, FetchInventoryDescendentsRequest);

                // justincc: I've disabled the CAPS service for now to fix problems with selecting textures, and
                // subsequent inventory breakage, in the edit object pane (such as mantis 1085).  This requires
                // enhancements (probably filling out the folder part of the LLSD reply) to our CAPS service,
                // but when I went on the Linden grid, the
                // simulators I visited (version 1.21) were, surprisingly, no longer supplying this capability.  Instead,
                // the 1.19.1.4 client appeared to be happily flowing inventory data over UDP
                //
                // This is very probably just a temporary measure - once the CAPS service appears again on the Linden grid
                // we will be
                // able to get the data we need to implement the necessary part of the protocol to fix the issue above.
                //                m_capsHandlers["FetchInventoryDescendents"] =
                //                    new RestStreamHandler("POST", capsBase + m_fetchInventoryPath, FetchInventoryRequest);

                // m_capsHandlers["FetchInventoryDescendents"] =
                //     new LLSDStreamhandler<LLSDFetchInventoryDescendents, LLSDInventoryDescendents>("POST",
                //                                                                                    capsBase + m_fetchInventory,
                //                                                                                    FetchInventory));
                // m_capsHandlers["RequestTextureDownload"] = new RestStreamHandler("POST",
                //                                                                  capsBase + m_requestTexture,
                //                                                                  RequestTexture);
            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: " + e.ToString());
            }
        }

        /// <summary>
        /// Construct a client response detailing all the capabilities this server can provide.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="httpRequest">HTTP request header object</param>
        /// <param name="httpResponse">HTTP response header object</param>
        /// <returns></returns>
        public string SeedCapRequest(string request, string path, string param,
                                  OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            m_log.Debug("[CAPS]: Seed Caps Request in region: " + m_regionName);

            if (!m_Scene.CheckClient(m_HostCapsObj.AgentID, httpRequest.RemoteIPEndPoint))
            {
                m_log.DebugFormat("[CAPS]: Unauthorized CAPS client");
                return string.Empty;
            }

            // WARNING: Add the external too
            string result = LLSDHelpers.SerialiseLLSDReply(m_HostCapsObj.CapsHandlers.CapsDetails);

            //m_log.DebugFormat("[CAPS] CapsRequest {0}", result);

            return result;
        }

        /// <summary>
        /// Called by the script task update handler.  Provides a URL to which the client can upload a new asset.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="httpRequest">HTTP request header object</param>
        /// <param name="httpResponse">HTTP response header object</param>
        /// <returns></returns>
        public string ScriptTaskInventory(string request, string path, string param,
                                          OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            try
            {
                m_log.Debug("[CAPS]: ScriptTaskInventory Request in region: " + m_regionName);
                //m_log.DebugFormat("[CAPS]: request: {0}, path: {1}, param: {2}", request, path, param);

                Hashtable hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
                LLSDTaskScriptUpdate llsdUpdateRequest = new LLSDTaskScriptUpdate();
                LLSDHelpers.DeserialiseOSDMap(hash, llsdUpdateRequest);

                string capsBase = "/CAPS/" + m_HostCapsObj.CapsObjectPath;
                string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

                TaskInventoryScriptUpdater uploader =
                    new TaskInventoryScriptUpdater(
                        llsdUpdateRequest.item_id,
                        llsdUpdateRequest.task_id,
                        llsdUpdateRequest.is_script_running,
                        capsBase + uploaderPath,
                        m_HostCapsObj.HttpListener,
                        m_dumpAssetsToFile);
                uploader.OnUpLoad += TaskScriptUpdated;

                m_HostCapsObj.HttpListener.AddStreamHandler(new BinaryStreamHandler("POST", capsBase + uploaderPath, uploader.uploaderCaps));

                string protocol = "http://";

                if (m_HostCapsObj.SSLCaps)
                    protocol = "https://";

                string uploaderURL = protocol + m_HostCapsObj.HostName + ":" + m_HostCapsObj.Port.ToString() + capsBase +
                                     uploaderPath;

                LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
                uploadResponse.uploader = uploaderURL;
                uploadResponse.state = "upload";

                //                m_log.InfoFormat("[CAPS]: " +
                //                                 "ScriptTaskInventory response: {0}",
                //                                 LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

                return LLSDHelpers.SerialiseLLSDReply(uploadResponse);
            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: " + e.ToString());
            }

            return null;
        }

        /// <summary>
        /// Called when new asset data for an agent inventory item update has been uploaded.
        /// </summary>
        /// <param name="itemID">Item to update</param>
        /// <param name="primID">Prim containing item to update</param>
        /// <param name="isScriptRunning">Signals whether the script to update is currently running</param>
        /// <param name="data">New asset data</param>
        public void TaskScriptUpdated(UUID itemID, UUID primID, bool isScriptRunning, byte[] data, ref ArrayList errors)
        {
            if (TaskScriptUpdatedCall != null)
            {
                ArrayList e = TaskScriptUpdatedCall(m_HostCapsObj.AgentID, itemID, primID, isScriptRunning, data);
                foreach (Object item in e)
                    errors.Add(item);
            }
        }

        public string UploadBakedTexture(string request, string path,
                string param, OSHttpRequest httpRequest,
                OSHttpResponse httpResponse)
        {
            try
            {
                //                m_log.Debug("[CAPS]: UploadBakedTexture Request in region: " +
                //                        m_regionName);

                string capsBase = "/CAPS/" + m_HostCapsObj.CapsObjectPath;
                string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

                BakedTextureUploader uploader =
                    new BakedTextureUploader(capsBase + uploaderPath, m_HostCapsObj.HttpListener);
                uploader.OnUpLoad += BakedTextureUploaded;

                m_HostCapsObj.HttpListener.AddStreamHandler(
                        new BinaryStreamHandler("POST", capsBase + uploaderPath,
                        uploader.uploaderCaps));

                string protocol = "http://";

                if (m_HostCapsObj.SSLCaps)
                    protocol = "https://";

                string uploaderURL = protocol + m_HostCapsObj.HostName + ":" +
                        m_HostCapsObj.Port.ToString() + capsBase + uploaderPath;

                LLSDAssetUploadResponse uploadResponse =
                        new LLSDAssetUploadResponse();
                uploadResponse.uploader = uploaderURL;
                uploadResponse.state = "upload";

                return LLSDHelpers.SerialiseLLSDReply(uploadResponse);
            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: " + e.ToString());
            }

            return null;
        }

        public void BakedTextureUploaded(UUID assetID, byte[] data)
        {
            //            m_log.WarnFormat("[CAPS]: Received baked texture {0}", assetID.ToString());

            AssetBase asset;
            asset = new AssetBase(assetID, "Baked Texture", (sbyte)AssetType.Texture, m_HostCapsObj.AgentID.ToString());
            asset.Data = data;
            asset.Temporary = true;
            asset.Local = !m_persistBakedTextures; // Local assets aren't persisted, non-local are
            m_assetService.Store(asset);
        }

        /// <summary>
        /// Called when new asset data for an agent inventory item update has been uploaded.
        /// </summary>
        /// <param name="itemID">Item to update</param>
        /// <param name="data">New asset data</param>
        /// <returns></returns>
        public UUID ItemUpdated(UUID itemID, byte[] data)
        {
            if (ItemUpdatedCall != null)
            {
                return ItemUpdatedCall(m_HostCapsObj.AgentID, itemID, data);
            }

            return UUID.Zero;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="llsdRequest"></param>
        /// <returns></returns>
        public LLSDAssetUploadResponse NewAgentInventoryRequest(LLSDAssetUploadRequest llsdRequest)
        {
            //m_log.Debug("[CAPS]: NewAgentInventoryRequest Request is: " + llsdRequest.ToString());
            //m_log.Debug("asset upload request via CAPS" + llsdRequest.inventory_type + " , " + llsdRequest.asset_type);

            if (llsdRequest.asset_type == "texture" ||
                llsdRequest.asset_type == "animation" ||
                llsdRequest.asset_type == "sound")
            {
                IClientAPI client = null;
                IScene scene = null;
                if (GetClient != null)
                {
                    client = GetClient(m_HostCapsObj.AgentID);
                    scene = client.Scene;

                    IMoneyModule mm = scene.RequestModuleInterface<IMoneyModule>();

                    if (mm != null)
                    {
                        if (!mm.UploadCovered(client, mm.UploadCharge))
                        {
                            if (client != null)
                                client.SendAgentAlertMessage("Unable to upload asset. Insufficient funds.", false);

                            LLSDAssetUploadResponse errorResponse = new LLSDAssetUploadResponse();
                            errorResponse.uploader = "";
                            errorResponse.state = "error";
                            return errorResponse;
                        }
                    }
                }
            }

            string assetName = llsdRequest.name;
            string assetDes = llsdRequest.description;
            string capsBase = "/CAPS/" + m_HostCapsObj.CapsObjectPath;
            UUID newAsset = UUID.Random();
            UUID newInvItem = UUID.Random();
            UUID parentFolder = llsdRequest.folder_id;
            string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

            AssetUploader uploader =
                new AssetUploader(assetName, assetDes, newAsset, newInvItem, parentFolder, llsdRequest.inventory_type,
                                  llsdRequest.asset_type, capsBase + uploaderPath, m_HostCapsObj.HttpListener, m_dumpAssetsToFile);
            m_HostCapsObj.HttpListener.AddStreamHandler(
                new BinaryStreamHandler("POST", capsBase + uploaderPath, uploader.uploaderCaps));

            string protocol = "http://";

            if (m_HostCapsObj.SSLCaps)
                protocol = "https://";

            string uploaderURL = protocol + m_HostCapsObj.HostName + ":" + m_HostCapsObj.Port.ToString() + capsBase +
                                 uploaderPath;

            LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
            uploadResponse.uploader = uploaderURL;
            uploadResponse.state = "upload";
            uploader.OnUpLoad += UploadCompleteHandler;
            return uploadResponse;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="assetID"></param>
        /// <param name="inventoryItem"></param>
        /// <param name="data"></param>
        public void UploadCompleteHandler(string assetName, string assetDescription, UUID assetID,
                                          UUID inventoryItem, UUID parentFolder, byte[] data, string inventoryType,
                                          string assetType)
        {
            sbyte assType = 0;
            sbyte inType = 0;

            if (inventoryType == "sound")
            {
                inType = 1;
                assType = 1;
            }
            else if (inventoryType == "animation")
            {
                inType = 19;
                assType = 20;
            }
            else if (inventoryType == "wearable")
            {
                inType = 18;
                switch (assetType)
                {
                    case "bodypart":
                        assType = 13;
                        break;
                    case "clothing":
                        assType = 5;
                        break;
                }
            }

            AssetBase asset;
            asset = new AssetBase(assetID, assetName, assType, m_HostCapsObj.AgentID.ToString());
            asset.Data = data;
            if (AddNewAsset != null)
                AddNewAsset(asset);
            else if (m_assetService != null)
                m_assetService.Store(asset);

            InventoryItemBase item = new InventoryItemBase();
            item.Owner = m_HostCapsObj.AgentID;
            item.CreatorId = m_HostCapsObj.AgentID.ToString();
            item.CreatorData = String.Empty;
            item.ID = inventoryItem;
            item.AssetID = asset.FullID;
            item.Description = assetDescription;
            item.Name = assetName;
            item.AssetType = assType;
            item.InvType = inType;
            item.Folder = parentFolder;
            item.CurrentPermissions = (uint)PermissionMask.All;
            item.BasePermissions = (uint)PermissionMask.All;
            item.EveryOnePermissions = 0;
            item.NextPermissions = (uint)(PermissionMask.Move | PermissionMask.Modify | PermissionMask.Transfer);
            item.CreationDate = Util.UnixTimeSinceEpoch();

            if (AddNewInventoryItem != null)
            {
                AddNewInventoryItem(m_HostCapsObj.AgentID, item);
            }
        }

        /// <summary>
        /// Processes a fetch inventory request and sends the reply

        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        // Request is like:
        //<llsd>
        //   <map><key>folders</key>
        //       <array>
        //            <map>
        //                <key>fetch-folders</key><boolean>1</boolean><key>fetch-items</key><boolean>1</boolean><key>folder-id</key><uuid>8e1e3a30-b9bf-11dc-95ff-0800200c9a66</uuid><key>owner-id</key><uuid>11111111-1111-0000-0000-000100bba000</uuid><key>sort-order</key><integer>1</integer>
        //            </map>
        //       </array>
        //   </map>
        //</llsd>
        //
        // multiple fetch-folder maps are allowed within the larger folders map.
        public string FetchInventoryRequest(string request, string path, string param)
        {
            // string unmodifiedRequest = request.ToString();

            //m_log.DebugFormat("[AGENT INVENTORY]: Received CAPS fetch inventory request {0}", unmodifiedRequest);
            m_log.Debug("[CAPS]: Inventory Request in region: " + m_regionName);

            Hashtable hash = new Hashtable();
            try
            {
                hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
            }
            catch (LLSD.LLSDParseException pe)
            {
                m_log.Error("[AGENT INVENTORY]: Fetch error: " + pe.Message);
                m_log.Error("Request: " + request.ToString());
            }

            ArrayList foldersrequested = (ArrayList)hash["folders"];

            string response = "";

            for (int i = 0; i < foldersrequested.Count; i++)
            {
                string inventoryitemstr = "";
                Hashtable inventoryhash = (Hashtable)foldersrequested[i];

                LLSDFetchInventoryDescendents llsdRequest = new LLSDFetchInventoryDescendents();
                LLSDHelpers.DeserialiseOSDMap(inventoryhash, llsdRequest);
                LLSDInventoryDescendents reply = FetchInventoryReply(llsdRequest);

                inventoryitemstr = LLSDHelpers.SerialiseLLSDReply(reply);
                inventoryitemstr = inventoryitemstr.Replace("<llsd><map><key>folders</key><array>", "");
                inventoryitemstr = inventoryitemstr.Replace("</array></map></llsd>", "");

                response += inventoryitemstr;
            }

            if (response.Length == 0)
            {
                // Ter-guess: If requests fail a lot, the client seems to stop requesting descendants.
                // Therefore, I'm concluding that the client only has so many threads available to do requests
                // and when a thread stalls..   is stays stalled.
                // Therefore we need to return something valid
                response = "<llsd><map><key>folders</key><array /></map></llsd>";
            }
            else
            {
                response = "<llsd><map><key>folders</key><array>" + response + "</array></map></llsd>";
            }

            //m_log.DebugFormat("[AGENT INVENTORY]: Replying to CAPS fetch inventory request with following xml");
            //m_log.Debug(Util.GetFormattedXml(response));

            return response;
        }

        public string FetchInventoryDescendentsRequest(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            // nasty temporary hack here, the linden client falsely
            // identifies the uuid 00000000-0000-0000-0000-000000000000
            // as a string which breaks us
            //
            // correctly mark it as a uuid
            //
            request = request.Replace("<string>00000000-0000-0000-0000-000000000000</string>", "<uuid>00000000-0000-0000-0000-000000000000</uuid>");

            // another hack <integer>1</integer> results in a
            // System.ArgumentException: Object type System.Int32 cannot
            // be converted to target type: System.Boolean
            //
            request = request.Replace("<key>fetch_folders</key><integer>0</integer>", "<key>fetch_folders</key><boolean>0</boolean>");
            request = request.Replace("<key>fetch_folders</key><integer>1</integer>", "<key>fetch_folders</key><boolean>1</boolean>");

            Hashtable hash = new Hashtable();
            try
            {
                hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
            }
            catch (LLSD.LLSDParseException pe)
            {
                m_log.Error("[AGENT INVENTORY]: Fetch error: " + pe.Message);
                m_log.Error("Request: " + request.ToString());
            }

            ArrayList foldersrequested = (ArrayList)hash["folders"];

            string response = "";
            lock (m_fetchLock)
            {
                for (int i = 0; i < foldersrequested.Count; i++)
                {
                    string inventoryitemstr = "";
                    Hashtable inventoryhash = (Hashtable)foldersrequested[i];

                    LLSDFetchInventoryDescendents llsdRequest = new LLSDFetchInventoryDescendents();

                    try
                    {
                        LLSDHelpers.DeserialiseOSDMap(inventoryhash, llsdRequest);
                    }
                    catch (Exception e)
                    {
                        m_log.Debug("[CAPS]: caught exception doing OSD deserialize" + e);
                    }
                    LLSDInventoryDescendents reply = FetchInventoryReply(llsdRequest);

                    inventoryitemstr = LLSDHelpers.SerialiseLLSDReply(reply);
                    inventoryitemstr = inventoryitemstr.Replace("<llsd><map><key>folders</key><array>", "");
                    inventoryitemstr = inventoryitemstr.Replace("</array></map></llsd>", "");

                    response += inventoryitemstr;
                }


                if (response.Length == 0)
                {
                    // Ter-guess: If requests fail a lot, the client seems to stop requesting descendants.
                    // Therefore, I'm concluding that the client only has so many threads available to do requests
                    // and when a thread stalls..   is stays stalled.
                    // Therefore we need to return something valid
                    response = "<llsd><map><key>folders</key><array /></map></llsd>";
                }
                else
                {
                    response = "<llsd><map><key>folders</key><array>" + response + "</array></map></llsd>";
                }

                //m_log.DebugFormat("[CAPS]: Replying to CAPS fetch inventory request with following xml");
                //m_log.Debug("[CAPS] "+response);

            }
            return response;
        }



        /// <summary>
        /// Construct an LLSD reply packet to a CAPS inventory request
        /// </summary>
        /// <param name="invFetch"></param>
        /// <returns></returns>
        private LLSDInventoryDescendents FetchInventoryReply(LLSDFetchInventoryDescendents invFetch)
        {
            LLSDInventoryDescendents reply = new LLSDInventoryDescendents();
            LLSDInventoryFolderContents contents = new LLSDInventoryFolderContents();
            contents.agent_id = m_HostCapsObj.AgentID;
            contents.owner_id = invFetch.owner_id;
            contents.folder_id = invFetch.folder_id;

            reply.folders.Array.Add(contents);
            InventoryCollection inv = new InventoryCollection();
            inv.Folders = new List<InventoryFolderBase>();
            inv.Items = new List<InventoryItemBase>();
            int version = 0;
            if (CAPSFetchInventoryDescendents != null)
            {
                inv = CAPSFetchInventoryDescendents(m_HostCapsObj.AgentID, invFetch.folder_id, invFetch.owner_id, invFetch.fetch_folders, invFetch.fetch_items, invFetch.sort_order, out version);
            }

            if (inv.Folders != null)
            {
                foreach (InventoryFolderBase invFolder in inv.Folders)
                {
                    contents.categories.Array.Add(ConvertInventoryFolder(invFolder));
                }
            }

            if (inv.Items != null)
            {
                foreach (InventoryItemBase invItem in inv.Items)
                {
                    contents.items.Array.Add(ConvertInventoryItem(invItem));
                }
            }

            contents.descendents = contents.items.Array.Count + contents.categories.Array.Count;
            contents.version = version;

            return reply;
        }

        /// <summary>
        /// Convert an internal inventory folder object into an LLSD object.
        /// </summary>
        /// <param name="invFolder"></param>
        /// <returns></returns>
        private LLSDInventoryFolder ConvertInventoryFolder(InventoryFolderBase invFolder)
        {
            LLSDInventoryFolder llsdFolder = new LLSDInventoryFolder();
            llsdFolder.folder_id = invFolder.ID;
            llsdFolder.parent_id = invFolder.ParentID;
            llsdFolder.name = invFolder.Name;
            if (invFolder.Type < 0 || invFolder.Type >= TaskInventoryItem.Types.Length)
                llsdFolder.type = "-1";
            else
                llsdFolder.type = TaskInventoryItem.Types[invFolder.Type];
            llsdFolder.preferred_type = "-1";

            return llsdFolder;
        }

        /// <summary>
        /// Convert an internal inventory item object into an LLSD object.
        /// </summary>
        /// <param name="invItem"></param>
        /// <returns></returns>
        private LLSDInventoryItem ConvertInventoryItem(InventoryItemBase invItem)
        {
            LLSDInventoryItem llsdItem = new LLSDInventoryItem();
            llsdItem.asset_id = invItem.AssetID;
            llsdItem.created_at = invItem.CreationDate;
            llsdItem.desc = invItem.Description;
            llsdItem.flags = (int)invItem.Flags;
            llsdItem.item_id = invItem.ID;
            llsdItem.name = invItem.Name;
            llsdItem.parent_id = invItem.Folder;
            try
            {
                // TODO reevaluate after upgrade to libomv >= r2566. Probably should use UtilsConversions.
                llsdItem.type = TaskInventoryItem.Types[invItem.AssetType];
                llsdItem.inv_type = TaskInventoryItem.InvTypes[invItem.InvType];
            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: Problem setting asset/inventory type while converting inventory item " + invItem.Name + " to LLSD:", e);
            }
            llsdItem.permissions = new LLSDPermissions();
            llsdItem.permissions.creator_id = invItem.CreatorIdAsUuid;
            llsdItem.permissions.base_mask = (int)invItem.CurrentPermissions;
            llsdItem.permissions.everyone_mask = (int)invItem.EveryOnePermissions;
            llsdItem.permissions.group_id = invItem.GroupID;
            llsdItem.permissions.group_mask = (int)invItem.GroupPermissions;
            llsdItem.permissions.is_owner_group = invItem.GroupOwned;
            llsdItem.permissions.next_owner_mask = (int)invItem.NextPermissions;
            llsdItem.permissions.owner_id = m_HostCapsObj.AgentID;
            llsdItem.permissions.owner_mask = (int)invItem.CurrentPermissions;
            llsdItem.sale_info = new LLSDSaleInfo();
            llsdItem.sale_info.sale_price = invItem.SalePrice;
            switch (invItem.SaleType)
            {
                default:
                    llsdItem.sale_info.sale_type = "not";
                    break;
                case 1:
                    llsdItem.sale_info.sale_type = "original";
                    break;
                case 2:
                    llsdItem.sale_info.sale_type = "copy";
                    break;
                case 3:
                    llsdItem.sale_info.sale_type = "contents";
                    break;
            }

            return llsdItem;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="mapReq"></param>
        /// <returns></returns>
        public LLSDMapLayerResponse GetMapLayer(LLSDMapRequest mapReq)
        {
            m_log.Debug("[CAPS]: MapLayer Request in region: " + m_regionName);
            LLSDMapLayerResponse mapResponse = new LLSDMapLayerResponse();
            mapResponse.LayerData.Array.Add(GetOSDMapLayerResponse());
            return mapResponse;
        }

        /// <summary>
        ///
        /// </summary>
        /// <returns></returns>
        protected static OSDMapLayer GetOSDMapLayerResponse()
        {
            OSDMapLayer mapLayer = new OSDMapLayer();
            mapLayer.Right = 5000;
            mapLayer.Top = 5000;
            mapLayer.ImageID = new UUID("00000000-0000-1111-9999-000000000006");

            return mapLayer;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string RequestTexture(string request, string path, string param)
        {
            m_log.Debug("texture request " + request);
            // Needs implementing (added to remove compiler warning)
            return String.Empty;
        }


        /// <summary>
        /// Called by the notecard update handler.  Provides a URL to which the client can upload a new asset.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string NoteCardAgentInventory(string request, string path, string param,
                                             OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            //m_log.Debug("[CAPS]: NoteCardAgentInventory Request in region: " + m_regionName + "\n" + request);
            //m_log.Debug("[CAPS]: NoteCardAgentInventory Request is: " + request);

            //OpenMetaverse.StructuredData.OSDMap hash = (OpenMetaverse.StructuredData.OSDMap)OpenMetaverse.StructuredData.LLSDParser.DeserializeBinary(Utils.StringToBytes(request));
            Hashtable hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
            LLSDItemUpdate llsdRequest = new LLSDItemUpdate();
            LLSDHelpers.DeserialiseOSDMap(hash, llsdRequest);

            string capsBase = "/CAPS/" + m_HostCapsObj.CapsObjectPath;
            string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

            ItemUpdater uploader =
                new ItemUpdater(llsdRequest.item_id, capsBase + uploaderPath, m_HostCapsObj.HttpListener, m_dumpAssetsToFile);
            uploader.OnUpLoad += ItemUpdated;

            m_HostCapsObj.HttpListener.AddStreamHandler(
                new BinaryStreamHandler("POST", capsBase + uploaderPath, uploader.uploaderCaps));

            string protocol = "http://";

            if (m_HostCapsObj.SSLCaps)
                protocol = "https://";

            string uploaderURL = protocol + m_HostCapsObj.HostName + ":" + m_HostCapsObj.Port.ToString() + capsBase +
                                 uploaderPath;

            LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
            uploadResponse.uploader = uploaderURL;
            uploadResponse.state = "upload";

            //            m_log.InfoFormat("[CAPS]: " +
            //                             "NoteCardAgentInventory response: {0}",
            //                             LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

            return LLSDHelpers.SerialiseLLSDReply(uploadResponse);
        }
    }

    public class AssetUploader
    {
        public event UpLoadedAsset OnUpLoad;
        private UpLoadedAsset handlerUpLoad = null;

        private string uploaderPath = String.Empty;
        private UUID newAssetID;
        private UUID inventoryItemID;
        private UUID parentFolder;
        private IHttpServer httpListener;
        private bool m_dumpAssetsToFile;
        private string m_assetName = String.Empty;
        private string m_assetDes = String.Empty;

        private string m_invType = String.Empty;
        private string m_assetType = String.Empty;

        public AssetUploader(string assetName, string description, UUID assetID, UUID inventoryItem,
                                UUID parentFolderID, string invType, string assetType, string path,
                                IHttpServer httpServer, bool dumpAssetsToFile)
        {
            m_assetName = assetName;
            m_assetDes = description;
            newAssetID = assetID;
            inventoryItemID = inventoryItem;
            uploaderPath = path;
            httpListener = httpServer;
            parentFolder = parentFolderID;
            m_assetType = assetType;
            m_invType = invType;
            m_dumpAssetsToFile = dumpAssetsToFile;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string uploaderCaps(byte[] data, string path, string param)
        {
            UUID inv = inventoryItemID;
            string res = String.Empty;
            LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
            uploadComplete.new_asset = newAssetID.ToString();
            uploadComplete.new_inventory_item = inv;
            uploadComplete.state = "complete";

            res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

            httpListener.RemoveStreamHandler("POST", uploaderPath);

            // TODO: probably make this a better set of extensions here
            string extension = ".jp2";
            if (m_invType != "image")
            {
                extension = ".dat";
            }

            if (m_dumpAssetsToFile)
            {
                SaveAssetToFile(m_assetName + extension, data);
            }
            handlerUpLoad = OnUpLoad;
            if (handlerUpLoad != null)
            {
                handlerUpLoad(m_assetName, m_assetDes, newAssetID, inv, parentFolder, data, m_invType, m_assetType);
            }

            return res;
        }
        ///Left this in and commented in case there are unforseen issues
        //private void SaveAssetToFile(string filename, byte[] data)
        //{
        //    FileStream fs = File.Create(filename);
        //    BinaryWriter bw = new BinaryWriter(fs);
        //    bw.Write(data);
        //    bw.Close();
        //    fs.Close();
        //}
        private static void SaveAssetToFile(string filename, byte[] data)
        {
            string assetPath = "UserAssets";
            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }
            FileStream fs = File.Create(Path.Combine(assetPath, Util.safeFileName(filename)));
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(data);
            bw.Close();
            fs.Close();
        }
    }

    /// <summary>
    /// This class is a callback invoked when a client sends asset data to
    /// an agent inventory notecard update url
    /// </summary>
    public class ItemUpdater
    {
        public event UpdateItem OnUpLoad;

        private UpdateItem handlerUpdateItem = null;

        private string uploaderPath = String.Empty;
        private UUID inventoryItemID;
        private IHttpServer httpListener;
        private bool m_dumpAssetToFile;

        public ItemUpdater(UUID inventoryItem, string path, IHttpServer httpServer, bool dumpAssetToFile)
        {
            m_dumpAssetToFile = dumpAssetToFile;

            inventoryItemID = inventoryItem;
            uploaderPath = path;
            httpListener = httpServer;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string uploaderCaps(byte[] data, string path, string param)
        {
            UUID inv = inventoryItemID;
            string res = String.Empty;
            LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
            UUID assetID = UUID.Zero;
            handlerUpdateItem = OnUpLoad;
            if (handlerUpdateItem != null)
            {
                assetID = handlerUpdateItem(inv, data);
            }

            uploadComplete.new_asset = assetID.ToString();
            uploadComplete.new_inventory_item = inv;
            uploadComplete.state = "complete";

            res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

            httpListener.RemoveStreamHandler("POST", uploaderPath);

            if (m_dumpAssetToFile)
            {
                SaveAssetToFile("updateditem" + Util.RandomClass.Next(1, 1000) + ".dat", data);
            }

            return res;
        }
        ///Left this in and commented in case there are unforseen issues
        //private void SaveAssetToFile(string filename, byte[] data)
        //{
        //    FileStream fs = File.Create(filename);
        //    BinaryWriter bw = new BinaryWriter(fs);
        //    bw.Write(data);
        //    bw.Close();
        //    fs.Close();
        //}
        private static void SaveAssetToFile(string filename, byte[] data)
        {
            string assetPath = "UserAssets";
            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }
            FileStream fs = File.Create(Path.Combine(assetPath, filename));
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(data);
            bw.Close();
            fs.Close();
        }
    }

    /// <summary>
    /// This class is a callback invoked when a client sends asset data to
    /// a task inventory script update url
    /// </summary>
    public class TaskInventoryScriptUpdater
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public event UpdateTaskScript OnUpLoad;

        private UpdateTaskScript handlerUpdateTaskScript = null;

        private string uploaderPath = String.Empty;
        private UUID inventoryItemID;
        private UUID primID;
        private bool isScriptRunning;
        private IHttpServer httpListener;
        private bool m_dumpAssetToFile;

        public TaskInventoryScriptUpdater(UUID inventoryItemID, UUID primID, int isScriptRunning,
                                            string path, IHttpServer httpServer, bool dumpAssetToFile)
        {
            m_dumpAssetToFile = dumpAssetToFile;

            this.inventoryItemID = inventoryItemID;
            this.primID = primID;

            // This comes in over the packet as an integer, but actually appears to be treated as a bool
            this.isScriptRunning = (0 == isScriptRunning ? false : true);

            uploaderPath = path;
            httpListener = httpServer;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string uploaderCaps(byte[] data, string path, string param)
        {
            try
            {
                //                    m_log.InfoFormat("[CAPS]: " +
                //                                     "TaskInventoryScriptUpdater received data: {0}, path: {1}, param: {2}",
                //                                     data, path, param));

                string res = String.Empty;
                LLSDTaskScriptUploadComplete uploadComplete = new LLSDTaskScriptUploadComplete();

                ArrayList errors = new ArrayList();
                handlerUpdateTaskScript = OnUpLoad;
                if (handlerUpdateTaskScript != null)
                {
                    handlerUpdateTaskScript(inventoryItemID, primID, isScriptRunning, data, ref errors);
                }

                uploadComplete.new_asset = inventoryItemID;
                uploadComplete.compiled = errors.Count > 0 ? false : true;
                uploadComplete.state = "complete";
                uploadComplete.errors = new OSDArray();
                uploadComplete.errors.Array = errors;

                res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

                httpListener.RemoveStreamHandler("POST", uploaderPath);

                if (m_dumpAssetToFile)
                {
                    SaveAssetToFile("updatedtaskscript" + Util.RandomClass.Next(1, 1000) + ".dat", data);
                }

                //                    m_log.InfoFormat("[CAPS]: TaskInventoryScriptUpdater.uploaderCaps res: {0}", res);

                return res;
            }
            catch (Exception e)
            {
                m_log.Error("[CAPS]: " + e.ToString());
            }

            // XXX Maybe this should be some meaningful error packet
            return null;
        }
        ///Left this in and commented in case there are unforseen issues
        //private void SaveAssetToFile(string filename, byte[] data)
        //{
        //    FileStream fs = File.Create(filename);
        //    BinaryWriter bw = new BinaryWriter(fs);
        //    bw.Write(data);
        //    bw.Close();
        //    fs.Close();
        //}
        private static void SaveAssetToFile(string filename, byte[] data)
        {
            string assetPath = "UserAssets";
            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }
            FileStream fs = File.Create(Path.Combine(assetPath, filename));
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(data);
            bw.Close();
            fs.Close();
        }
    }

    public class BakedTextureUploader
    {
        public event UploadedBakedTexture OnUpLoad;
        private UploadedBakedTexture handlerUpLoad = null;

        private string uploaderPath = String.Empty;
        private UUID newAssetID;
        private IHttpServer httpListener;

        public BakedTextureUploader(string path, IHttpServer httpServer)
        {
            newAssetID = UUID.Random();
            uploaderPath = path;
            httpListener = httpServer;
            //                m_log.InfoFormat("[CAPS] baked texture upload starting for {0}",newAssetID);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public string uploaderCaps(byte[] data, string path, string param)
        {
            handlerUpLoad = OnUpLoad;
            if (handlerUpLoad != null)
            {
                Util.FireAndForget(delegate(object o) { handlerUpLoad(newAssetID, data); });
            }

            string res = String.Empty;
            LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
            uploadComplete.new_asset = newAssetID.ToString();
            uploadComplete.new_inventory_item = UUID.Zero;
            uploadComplete.state = "complete";

            res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

            httpListener.RemoveStreamHandler("POST", uploaderPath);

            //                m_log.InfoFormat("[CAPS] baked texture upload completed for {0}",newAssetID);

            return res;
        }
    }

}