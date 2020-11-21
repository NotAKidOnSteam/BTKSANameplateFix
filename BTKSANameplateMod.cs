﻿using Harmony;
using Il2CppSystem.Text;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UIExpansionKit.API;
using UnhollowerRuntimeLib;
using UnityEngine;
using VRC;
using VRC.Core;
using VRC.SDKBase;

namespace BTKSANameplateMod
{
    public static class BuildInfo
    {
        public const string Name = "BTKSANameplateMod"; // Name of the Mod.  (MUST BE SET)
        public const string Author = "DDAkebono#0001"; // Author of the Mod.  (Set as null if none)
        public const string Company = "BTK-Development"; // Company that made the Mod.  (Set as null if none)
        public const string Version = "1.3.3"; // Version of the Mod.  (MUST BE SET)
        public const string DownloadLink = "https://github.com/ddakebono/BTKSANameplateFix/releases"; // Download Link for the Mod.  (Set as null if none)
    }

    public class BTKSANameplateMod : MelonMod
    {
        public static BTKSANameplateMod instance;

        public HarmonyInstance harmony;

        public float nameplateDefaultSize = 0.0015f;
        public float customNameplateDefaultSize = 1.0f;
        public bool isInit = false;

        private string settingsCategory = "BTKSANameplateFix";
        private string hiddenCustomSetting = "enableHiddenCustomNameplates";
        private string hideFriendsNameplates = "hideFriendsNameplates";
        private string hideNameplateBorder = "hideNameplateBorder";
        private string nameplateScaleSetting = "nameplateScale";
        private string dynamicResizerSetting = "dynamicResizer";
        private string dynamicResizerDistance = "dynamicResizerDist";

        //Helper PropertyInfo
        PropertyInfo avatarDescriptProperty;

        //Save prefs copy to compare for ReloadAllAvatars
        bool hiddenCustomLocal = false;
        bool hideFriendsLocal = false;
        bool hideNameplateLocal = false;
        int scaleLocal = 100;
        bool dynamicResizerLocal = false;
        float dynamicResDistLocal = 3f;


        List<string> hiddenNameplateUserIDs = new List<string>();

        //Assets
        AssetBundle shaderBundle;
        Shader borderShader;
        Shader tagShader;

        public override void VRChat_OnUiManagerInit()
        {
            Log("BTK Standalone: Nameplate Mod - Starting up");

            instance = this;

            if (MelonHandler.Mods.Any(x => x.Info.Name.Equals("BTKCompanionLoader", StringComparison.OrdinalIgnoreCase)))
            {
                MelonLogger.Log("Hold on a sec! Looks like you've got BTKCompanion installed, this mod is built in and not needed!");
                MelonLogger.LogError("BTKSANameplateMod has not started up! (BTKCompanion Running)");
                return;
            }

            MelonPrefs.RegisterCategory(settingsCategory, "Nameplate Mod");
            MelonPrefs.RegisterBool(settingsCategory, hiddenCustomSetting, false, "Enable Hidden Custom Nameplates");
            MelonPrefs.RegisterBool(settingsCategory, hideFriendsNameplates, false, "Hide Friends Nameplates");
            MelonPrefs.RegisterBool(settingsCategory, hideNameplateBorder, false, "Hide Nameplate Borders");
            MelonPrefs.RegisterInt(settingsCategory, nameplateScaleSetting, 100, "Nameplate Size Percentage");
            MelonPrefs.RegisterBool(settingsCategory, dynamicResizerSetting, false, "Enable Dynamic Nameplate Resizer");
            MelonPrefs.RegisterFloat(settingsCategory, dynamicResizerDistance, 3f, "Dynamic Resizer Max Distance");

            //Register dynamic scaler
            ClassInjector.RegisterTypeInIl2Cpp<DynamicScaler>();
            ClassInjector.RegisterTypeInIl2Cpp<DynamicScalerCustom>();

            //Register our menu button
            if(MelonHandler.Mods.Any(x => x.Info.Name.Equals("UI Expansion Kit", StringComparison.OrdinalIgnoreCase)))
                ExpansionKitApi.RegisterSimpleMenuButton(ExpandedMenu.UserQuickMenu, "Toggle Nameplate Visibility", ToggleNameplateVisiblity);

            //Initalize Harmony
            harmony = HarmonyInstance.Create("BTKStandalone");

            foreach (MethodInfo method in typeof(VRCPlayer).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.XRefScanForGlobal("Avatar is ready, Initializing"))
                {
                    Log($"Target method found {method.Name}", true);
                    harmony.Patch(method, null, new HarmonyMethod(typeof(BTKSANameplateMod).GetMethod("OnAvatarInit", BindingFlags.Public | BindingFlags.Static)));
                    break;
                }
            }

            avatarDescriptProperty = typeof(VRCAvatarManager).GetProperty("prop_VRC_AvatarDescriptor_0", BindingFlags.Public | BindingFlags.Instance, null, typeof(VRC_AvatarDescriptor), new Type[0], null);

            Log("Loading Assets from Embedded Bundle...");
            loadAssets();

            Log("Loading HiddenNameplateUserIDs from file", true);
            LoadHiddenNameplateFromFile();
            //Load the settings to the local copy to compare with SettingsApplied
            getPrefsLocal();
        }


        public override void OnModSettingsApplied()
        {
            if (hiddenCustomLocal != MelonPrefs.GetBool(settingsCategory, hiddenCustomSetting) || hideFriendsLocal != MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates) || scaleLocal != MelonPrefs.GetInt(settingsCategory, nameplateScaleSetting) || dynamicResizerLocal != MelonPrefs.GetBool(settingsCategory, dynamicResizerSetting) || dynamicResDistLocal != MelonPrefs.GetFloat(settingsCategory, dynamicResizerDistance) || hideNameplateLocal != MelonPrefs.GetBool(settingsCategory, hideNameplateBorder))
                VRCPlayer.field_Internal_Static_VRCPlayer_0.Method_Public_Void_Boolean_0();

            getPrefsLocal();
        }

        public void OnUpdatePlayer(Player player)
        {
            if (ValidatePlayerAvatar(player))
            {
                Log($"VRCPlayer is {player.field_Internal_VRCPlayer_0!=null}", true);

                GDBUser user = new GDBUser(player);
                bool friend = false;
                if (!player.name.Contains("Local"))
                {
                    if (user.vrcPlayer.field_Internal_VRCPlayer_0.field_Private_VRCWorldPlayerUiProfile_0 == null)
                        return;

                    GameObject nameplate = user.vrcPlayer.field_Internal_VRCPlayer_0.field_Private_VRCWorldPlayerUiProfile_0.gameObject;
                    Transform customNameplateObject = user.avatarObject.transform.Find("Custom Nameplate");
                    Transform tagAndBGObj = null;
                    Transform borderObj = null;

                    //Reset Nameplate to default state and remove DynamicNameplateScalers
                    resetNameplate(nameplate, nameplateDefaultSize);

                    //Reset Custom Nameplate scale
                    if (customNameplateObject != null)
                    {
                        tagAndBGObj = customNameplateObject.Find("Tag and Background");
                        borderObj = customNameplateObject.Find("Border");
                        resetNameplate(customNameplateObject.gameObject, customNameplateDefaultSize, tagAndBGObj, borderObj);
                    }

                    if (player.field_Private_APIUser_0 != null)
                    {
                        //Check if the Nameplate should be hidden
                        if (hiddenNameplateUserIDs.Contains(player.field_Private_APIUser_0.id))
                        {
                            nameplate.transform.localScale = Vector3.zero;
                            if (customNameplateObject != null)
                                customNameplateObject.gameObject.SetActive(false);
                            return;
                        }
                    }

                    ////
                    /// Nameplate RNG Fix
                    ////

                    //User is remote, apply fix
                    Log($"New user or avatar change! Applying NameplateMod on { player.name }", true);
                    Vector3 npPos = nameplate.transform.position;
                    object avatarDescriptor = avatarDescriptProperty.GetValue(user.vrcPlayer.prop_VRCPlayer_0.prop_VRCAvatarManager_0);
                    float viewPointY = 0;

                    //Get viewpoint for AV2 avatar
                    if (avatarDescriptor != null)
                        viewPointY = ((VRC_AvatarDescriptor)avatarDescriptor).ViewPosition.y;

                    //Get viewpoint for AV3 avatar
                    if (user.vrcPlayer.prop_VRCPlayer_0.prop_VRCAvatarManager_0.prop_VRCAvatarDescriptor_0 != null)
                        viewPointY = user.vrcPlayer.prop_VRCPlayer_0.prop_VRCAvatarManager_0.prop_VRCAvatarDescriptor_0.ViewPosition.y;

                    if (viewPointY > 0)
                    {
                        npPos.y = viewPointY + user.vrcPlayer.transform.position.y + 0.5f;
                        nameplate.transform.position = npPos;
                    }

                    ////
                    /// Player nameplate checks
                    ////

                    float nameplateScale = (MelonPrefs.GetInt(settingsCategory, nameplateScaleSetting) / 100f) * 0.0015f;

                    //Disable nameplates on friends
                    if (MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates))
                    {
                        if (player.field_Private_APIUser_0 != null)
                        {
                            if (APIUser.IsFriendsWith(player.field_Private_APIUser_0.id))
                            {
                                Vector3 newScale = new Vector3(0, 0, 0);
                                nameplate.transform.localScale = newScale;
                                friend = true;
                            }
                        }
                    }

                    //Setup static or dynamic scale
                    applyScale(user, nameplate, nameplateScale, nameplateDefaultSize, friend);


                    ////
                    /// Custom Nameplate Checks
                    ////

                    //Grab custom nameplate object for next 2 checks
                    float customNameplateScale = (MelonPrefs.GetInt(settingsCategory, nameplateScaleSetting) / 100f) * 1.0f;

                    //Enable Hidden Custom Nameplate
                    if (MelonPrefs.GetBool(settingsCategory, hiddenCustomSetting) && !(MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates) && friend))
                    {

                        if (customNameplateObject != null && !customNameplateObject.gameObject.active)
                        {
                            Log($"Found hidden Custom Nameplate on { player.name }, enabling.", true);
                            customNameplateObject.gameObject.SetActive(true);
                        }
                    }

                    //Check if nameplate should be hidden or resized
                    if (customNameplateObject != null && customNameplateObject.gameObject.active)
                    {
                        if (MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates) && friend)
                            customNameplateObject.gameObject.SetActive(false);

                        if (tagAndBGObj != null && borderObj != null)
                        {
                            SkinnedMeshRenderer tagRenderer = tagAndBGObj.gameObject.GetComponent<SkinnedMeshRenderer>();
                            SkinnedMeshRenderer borderRenderer = borderObj.gameObject.GetComponent<SkinnedMeshRenderer>();

                            //Replace shaders!
                            replaceCustomNameplateShader(tagRenderer, borderRenderer);

                            //Apply scaler
                            applyScale(user, customNameplateObject.gameObject, customNameplateScale, customNameplateDefaultSize, friend, true, tagRenderer, borderRenderer);
                        }
                    }

                    ////
                    /// Nameplate Misc Mods
                    ////

                    if (MelonPrefs.GetBool(settingsCategory, hideNameplateBorder))
                    {
                        Transform border = nameplate.transform.Find("Frames");
                        if (border != null)
                        {
                            border.gameObject.active = false;
                        }
                    }

                }
            }
        }

        /// <summary>
        /// Applies either static scale or DynamicScaler to target object
        /// </summary>
        /// <param name="user">Target player</param>
        /// <param name="target">Target GameObject</param>
        /// <param name="nameplateScale">New scale or target min scale</param>
        /// <param name="defaultSize">Default size of the target GameObject</param>
        /// <param name="isFriend"></param>
        private void applyScale(GDBUser user, GameObject target, float nameplateScale, float defaultSize, bool isFriend, bool isCustomNameplate = false, SkinnedMeshRenderer tagRenderer = null, SkinnedMeshRenderer borderRenderer = null)
        {
            if (nameplateScale != nameplateDefaultSize && !(isFriend && MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates)))
            {
                if (MelonPrefs.GetBool(settingsCategory, dynamicResizerSetting))
                {
                    if (!isCustomNameplate)
                    {
                        DynamicScaler component = target.AddComponent<DynamicScaler>();
                        component.ApplySettings(user.vrcPlayer, target, nameplateScale, defaultSize, MelonPrefs.GetFloat(settingsCategory, dynamicResizerDistance));
                    }
                    else
                    {
                        DynamicScalerCustom component = target.AddComponent<DynamicScalerCustom>();
                        if (tagRenderer != null && borderRenderer!=null)
                        {
                            component.ApplySettings(user.vrcPlayer, borderRenderer.material, tagRenderer.material, nameplateScale, defaultSize, MelonPrefs.GetFloat(settingsCategory, dynamicResizerDistance));
                        }
                    }
                }
                else
                {
                    if (!isCustomNameplate)
                    {
                        Vector3 newScale = new Vector3(nameplateScale, nameplateScale, nameplateScale);
                        target.transform.localScale = newScale;
                    }
                    else
                    {
                        if (tagRenderer != null)
                        {
                            tagRenderer.material.shader = tagShader;
                            tagRenderer.material.SetFloat("_Scale", nameplateScale);
                        }

                        if (borderRenderer != null)
                        {
                            borderRenderer.material.shader = borderShader;
                            borderRenderer.material.SetFloat("_Scale", nameplateScale);
                        }
                    }
                }
            }
        }

        private void replaceCustomNameplateShader(SkinnedMeshRenderer tagRenderer, SkinnedMeshRenderer borderRenderer)
        {
            if (tagRenderer != null)
            {
                tagRenderer.material.shader = tagShader;
                tagRenderer.material.SetFloat("_Scale", 1.0f);
            }

            if (borderRenderer != null)
            {
                borderRenderer.material.shader = borderShader;
                borderRenderer.material.SetFloat("_Scale", 1.0f);
            }
        }

        private void resetNameplate(GameObject nameplate, float defaultSize, Transform tagObj = null, Transform borderObj = null)
        {
            foreach (DynamicScaler scaler in nameplate.GetComponents<DynamicScaler>())
            {
                GameObject.Destroy(scaler);
            }

            foreach(DynamicScalerCustom scaler in nameplate.GetComponents<DynamicScalerCustom>())
            {
                GameObject.Destroy(scaler);
            }

            nameplate.transform.localScale = new Vector3(defaultSize, defaultSize, defaultSize);

            //Reset Border Disable
            Transform border = nameplate.transform.Find("Frames");
            if (border != null)
            {
                border.gameObject.active = true;
            }

            if (tagObj != null)
            {
                SkinnedMeshRenderer tagRenderer = tagObj.gameObject.GetComponent<SkinnedMeshRenderer>();
                tagRenderer.material.SetFloat("_Scale", defaultSize);
            }

            if(borderObj != null)
            {
                SkinnedMeshRenderer borderRenderer = borderObj.gameObject.GetComponent<SkinnedMeshRenderer>();
                borderRenderer.material.SetFloat("_Scale", defaultSize);
            }
        }

        private void loadAssets()
        {
            using (var assetStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BTKSANameplateMod.assets"))
            {
                Log("Loaded Embedded resource", true);
                using (var tempStream = new MemoryStream((int)assetStream.Length))
                {
                    assetStream.CopyTo(tempStream);

                    shaderBundle = AssetBundle.LoadFromMemory_Internal(tempStream.ToArray(), 0);
                    shaderBundle.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                }
            }

            if (shaderBundle != null)
            {
                borderShader = shaderBundle.LoadAsset_Internal("CustomBorder", Il2CppType.Of<Shader>()).Cast<Shader>();
                borderShader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                tagShader = shaderBundle.LoadAsset_Internal("CustomTag", Il2CppType.Of<Shader>()).Cast<Shader>();
                tagShader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }

            Log("Loaded Assets Successfully!", true);
        }

        private void ToggleNameplateVisiblity()
        {
            if(!hiddenNameplateUserIDs.Contains(QuickMenu.prop_QuickMenu_0.field_Private_APIUser_0.id))
                hiddenNameplateUserIDs.Add(QuickMenu.prop_QuickMenu_0.field_Private_APIUser_0.id);
            else
                hiddenNameplateUserIDs.Remove(QuickMenu.prop_QuickMenu_0.field_Private_APIUser_0.id);

            SaveHiddenNameplateFile();
            OnUpdatePlayer(getPlayerFromPlayerlist(QuickMenu.prop_QuickMenu_0.field_Private_APIUser_0.id));
        }

        private void SaveHiddenNameplateFile()
        {
            StringBuilder builder = new StringBuilder();
            foreach(string id in hiddenNameplateUserIDs)
            {
                builder.Append(id);
                builder.AppendLine();
            }
            File.WriteAllText("UserData\\BTKHiddenNameplates.txt", builder.ToString());
        }

        private void LoadHiddenNameplateFromFile()
        {
            if (File.Exists("UserData\\BTKHiddenNameplates.txt"))
            {
                hiddenNameplateUserIDs.Clear();

                string[] lines = File.ReadAllLines("UserData\\BTKHiddenNameplates.txt");

                foreach (string line in lines)
                {
                    if (!String.IsNullOrWhiteSpace(line))
                        hiddenNameplateUserIDs.Add(line);
                }
            }
        }

        private void getPrefsLocal()
        {
            hiddenCustomLocal = MelonPrefs.GetBool(settingsCategory, hiddenCustomSetting);
            hideFriendsLocal = MelonPrefs.GetBool(settingsCategory, hideFriendsNameplates);
            scaleLocal = MelonPrefs.GetInt(settingsCategory, nameplateScaleSetting);
            dynamicResizerLocal = MelonPrefs.GetBool(settingsCategory, dynamicResizerSetting);
            dynamicResDistLocal = MelonPrefs.GetFloat(settingsCategory, dynamicResizerDistance);
            hideNameplateLocal = MelonPrefs.GetBool(settingsCategory, hideNameplateBorder);
        }

        public static void OnAvatarInit(GameObject __0, VRC_AvatarDescriptor __1, bool __2)
        {
            Log($"Hit OnAvatarInit ad:{__1 != null} state:{__2}", true);

            if (__1 != null)
            {
                foreach (Player player in PlayerManager.field_Private_Static_PlayerManager_0.field_Private_List_1_Player_0)
                {
                    VRCPlayer vrcPlayer = player.field_Internal_VRCPlayer_0;
                    if (vrcPlayer == null)
                        continue;

                    VRCAvatarManager vrcAM = vrcPlayer.prop_VRCAvatarManager_0;
                    if (vrcAM == null)
                        continue;

                    VRC_AvatarDescriptor descriptor = vrcAM.prop_VRC_AvatarDescriptor_0;
                    if ((descriptor == null) || (descriptor != __1))
                        continue;

                    BTKSANameplateMod.instance.OnUpdatePlayer(player);
                    break;
                }
            }
        }

        public static void Log(string log, bool dbg = false)
        {
            if (!Imports.IsDebugMode() && dbg)
                return;

            MelonLogger.Log(log);
        }

        public static Player getPlayerFromPlayerlist(string userID)
        {
            foreach (var player in PlayerManager.field_Private_Static_PlayerManager_0.field_Private_List_1_Player_0)
            {
                if (player.field_Private_APIUser_0 != null)
                {
                    if (player.field_Private_APIUser_0.id.Equals(userID))
                        return player;
                }
            }
            return null;
        }

        bool ValidatePlayerAvatar(Player player)
        {
            return !(player == null ||
                     player.field_Internal_VRCPlayer_0 == null ||
                     player.field_Internal_VRCPlayer_0.isActiveAndEnabled == false ||
                     player.field_Internal_VRCPlayer_0.field_Internal_Animator_0 == null ||
                     player.field_Internal_VRCPlayer_0.field_Internal_GameObject_0 == null ||
                     player.field_Internal_VRCPlayer_0.field_Internal_GameObject_0.name.IndexOf("Avatar_Utility_Base_") == 0);
        }

    }
}
