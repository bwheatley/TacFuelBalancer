﻿/**
 * FuelBalanceController.cs
 * 
 * Thunder Aerospace Corporation's Fuel Balancer for the Kerbal Space Program, by Taranis Elsu
 * 
 * (C) Copyright 2013, Taranis Elsu
 * 
 * Kerbal Space Program is Copyright (C) 2013 Squad. See http://kerbalspaceprogram.com/. This
 * project is in no way associated with nor endorsed by Squad.
 * 
 * This code is licensed under the Attribution-NonCommercial-ShareAlike 3.0 (CC BY-NC-SA 3.0)
 * creative commons license. See <http://creativecommons.org/licenses/by-nc-sa/3.0/legalcode>
 * for full details.
 * 
 * Attribution — You are free to modify this code, so long as you mention that the resulting
 * work is based upon or adapted from this code.
 * 
 * Non-commercial - You may not use this work for commercial purposes.
 * 
 * Share Alike — If you alter, transform, or build upon this work, you may distribute the
 * resulting work only under the same or similar license to the CC BY-NC-SA 3.0 license.
 * 
 * Note that Thunder Aerospace Corporation is a ficticious entity created for entertainment
 * purposes. It is in no way meant to represent a real entity. Any similarity to a real entity
 * is purely coincidental.
 */

using KSP.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Tac
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    sealed class FuelBalanceController : MonoBehaviour
    {
        private const int MaxRecentVessels = 5;
        private const double AmountEpsilon = 0.001, PercentEpsilon = 0.00001;
        
        sealed class VesselInfo
        {
            public VesselInfo()
            {
                resources     = new Dictionary<string, ResourceInfo>();
                lastPartCount = 0;
                lastSituation = Vessel.Situations.PRELAUNCH;
            }
            
            public VesselInfo(Vessel vessel)
            {
                this.vessel        = vessel;
                this.resources     = new Dictionary<string, ResourceInfo>();
                this.lastSituation = vessel.situation;
                this.lastPartCount = vessel.parts.Count;
            }

            public readonly Vessel vessel;
            public Dictionary<string, ResourceInfo> resources;
            public Vessel.Situations lastSituation;
            public int lastPartCount;
        }

        private Settings settings;
        private MainWindow mainWindow;
        private SettingsWindow settingsWindow;
        private HelpWindow helpWindow;
        private string configFilename;
        private ButtonWrapper button;
        private VesselInfo vesselInfo;
        private readonly List<VesselInfo> recentVessels = new List<VesselInfo>(MaxRecentVessels);

        void Awake()
        {
            this.Log("Awake");
            configFilename = IOUtils.GetFilePathFor(this.GetType(), "FuelBalancer.cfg");

            settings = new Settings();

            settingsWindow = new SettingsWindow(settings);
            helpWindow = new HelpWindow();
            mainWindow = new MainWindow(this, settings, settingsWindow, helpWindow);

            button = new ButtonWrapper(new Rect(Screen.width * 0.7f, 0, 32, 32),
                "TacFuelBalancer/Textures/button", "FB",
                "TAC Fuel Balancer", OnIconClicked);
            
            vesselInfo = new VesselInfo();
        }

        void Start()
        {
            this.Log("Start");
            Load();

            button.Visible = true;

            // Make sure the resource/part list is correct after other mods, such as StretchyTanks, do their thing.
            Invoke("RebuildActiveVesselLists", 1.0f);
        }

        void OnDestroy()
        {
            this.Log("OnDestroy");
            Save();
            button.Destroy();
        }

        void Update()
        {
            foreach (ResourceInfo resourceInfo in vesselInfo.resources.Values)
            {
                foreach (ResourcePartMap partInfo in resourceInfo.parts)
                {
                    if (partInfo.isHighlighted || mainWindow.IsVisible() && resourceInfo.isShowing && partInfo.isSelected)
                    {
                        partInfo.part.SetHighlightColor(Color.blue);
                        partInfo.part.SetHighlight(true, false);
                    }
                }
            }
        }

        void FixedUpdate()
        {
            if (!FlightGlobals.ready)
            {
                this.Log("FlightGlobals are not valid yet.");
                return;
            }

            Vessel activeVessel = FlightGlobals.fetch.activeVessel;
            if (activeVessel == null)
            {
                this.Log("No active vessel yet.");
                return;
            }
            else if (activeVessel.isEVA)
            {
                button.Visible = false;
                mainWindow.SetVisible(false);
                return;
            }
            else if (!button.Visible)
            {
                button.Visible = true;
            }

            if (activeVessel != vesselInfo.vessel || activeVessel.situation != vesselInfo.lastSituation || activeVessel.Parts.Count != vesselInfo.lastPartCount)
            {
                RebuildLists(activeVessel);
            }

            if (!HasPower())
            {
                return;
            }

            // Do any fuel transfers
            double maxFuelFlow = settings.MaxFuelFlow * settings.RateMultiplier * TimeWarp.fixedDeltaTime;
            foreach (ResourceInfo resourceInfo in vesselInfo.resources.Values)
            {
                foreach (ResourcePartMap partInfo in resourceInfo.parts)
                {
                    SynchronizeFlowState(partInfo);
                }
                
                foreach (ResourcePartMap partInfo in resourceInfo.parts)
                {
                    if (partInfo.direction == TransferDirection.IN)
                    {
                        TransferIn(maxFuelFlow, resourceInfo, partInfo);
                    }
                    else if (partInfo.direction == TransferDirection.OUT)
                    {
                        TransferOut(maxFuelFlow, resourceInfo, partInfo);
                    }
                    else if (partInfo.direction == TransferDirection.DUMP)
                    {
                        DumpOut(maxFuelFlow, resourceInfo, partInfo);
                    }
                }

                BalanceResources(maxFuelFlow, resourceInfo.parts.FindAll(
                    rpm => rpm.direction == TransferDirection.BALANCE || (resourceInfo.balance && rpm.direction == TransferDirection.NONE)));

                if (settings.BalanceIn)
                {
                    BalanceResources(maxFuelFlow, resourceInfo.parts.FindAll(rpm => rpm.direction == TransferDirection.IN));
                }
                if (settings.BalanceOut)
                {
                    BalanceResources(maxFuelFlow, resourceInfo.parts.FindAll(rpm => rpm.direction == TransferDirection.OUT));
                }
            }
        }


		/// <summary>
		/// Called by Unity to draw the GUI - can be called many times per frame.
		/// </summary>
		public void OnGUI( )
		{
			mainWindow.DrawWindow( );
			settingsWindow.DrawWindow( );
			helpWindow.DrawWindow( );
			button.Draw( );
		}



        /*
         * Checks the PartResource's flow state (controlled from the part's right click menu), and makes our state match its state.
         */
        private static void SynchronizeFlowState(ResourcePartMap partInfo)
        {
            if (!partInfo.resource.Locked && partInfo.direction == TransferDirection.LOCKED)
            {
                partInfo.direction = TransferDirection.NONE;
            }
            else if (partInfo.resource.Locked && partInfo.direction != TransferDirection.LOCKED)
            {
                partInfo.direction = TransferDirection.LOCKED;
            }
        }

        public Dictionary<string, ResourceInfo> GetResourceInfo()
        {
            return vesselInfo.resources;
        }

        public bool IsPrelaunch()
        {
            return (vesselInfo.vessel.mainBody == FlightGlobals.Bodies[1]) &&
                (vesselInfo.vessel.situation == Vessel.Situations.PRELAUNCH || vesselInfo.vessel.situation == Vessel.Situations.LANDED);
        }

        public bool IsControllable()
        {
            return vesselInfo.vessel.IsControllable && HasPower();
        }

        public bool HasPower()
        {
            ResourceInfo electric;
            return vesselInfo.resources.TryGetValue("ElectricCharge", out electric) &&
                   electric.parts.Any(p => p.resource.Amount > 0.01);
        }

        public void SortParts(Comparison<ResourcePartMap> comparer)
        {
            foreach (ResourceInfo resource in vesselInfo.resources.Values)
            {
                // we need a stable sort, but the built-in .NET sorting methods are unstable, so we'll use insertion sort
                List<ResourcePartMap> parts = resource.parts;
                for (int i=1; i < parts.Count; i++)
                {
                    ResourcePartMap part = parts[i];
                    int j;
                    for (j=i; j > 0 && comparer(parts[j-1], part) > 0; j--)
                    {
                        parts[j] = parts[j-1];
                    }
                    parts[j] = part;
                }
            }
        }
        
        private void Load()
        {
            if (File.Exists<FuelBalanceController>(configFilename))
            {
                ConfigNode config = ConfigNode.Load(configFilename);
                settings.Load(config);
                button.Load(config);
                mainWindow.Load(config);
                settingsWindow.Load(config);
                helpWindow.Load(config);
            }
        }

        private void Save()
        {
            ConfigNode config = new ConfigNode();
            settings.Save(config);
            button.Save(config);
            mainWindow.Save(config);
            settingsWindow.Save(config);
            helpWindow.Save(config);

            config.Save(configFilename);
        }

        private void OnIconClicked()
        {
            mainWindow.ToggleVisible();
        }

        private void AddResource(string resourceName, Part part, Dictionary<object,int> shipIds, Func<Part,string,PartResourceInfo> infoCreator)
        {
            ResourceInfo resourceInfo;
            if (!vesselInfo.resources.TryGetValue(resourceName, out resourceInfo))
            {
                vesselInfo.resources[resourceName] = resourceInfo = new ResourceInfo(GetResourceTitle(resourceName));
            }
            List<ResourcePartMap> resourceParts = resourceInfo.parts;
            ResourcePartMap partInfo = resourceParts.Find(info => info.part.Equals(part));
            if (partInfo == null)
            {
                resourceParts.Add(new ResourcePartMap(infoCreator(part, resourceName), part, shipIds[part]));
            }
            else
            {
                // Make sure we are still pointing at the right resource instance. This is a fix for compatibility with StretchyTanks.
                partInfo.resource.Refresh(part);
            }
        }
        
        private static string GetResourceTitle(string resourceName)
        {
            switch (resourceName)
            {
                case "_RocketFuel": return "Rocket";
                case "ElectricCharge": return "Electric";
                case "LiquidFuel": return "Liquid";
                case "MonoPropellant": return "RCS";
                case "XenonGas": return "Xenon";
                default: return resourceName;
            }
        }

        private void RebuildLists(Vessel vessel)
        {
            this.Log("Rebuilding resource lists.");
            // try to restore the old vessel info if we're switching vessels
            if (vesselInfo.vessel != vessel)
            {
                recentVessels.RemoveAll(v => !FlightGlobals.Vessels.Contains(v.vessel)); // remove information about dead vessels
                int index = recentVessels.FindIndex(v => v.vessel == vessel);
                if (vesselInfo.vessel != null) // save the current data if it's not the initial, uninitialized state
                {
                    recentVessels.Add(vesselInfo);
                }
                if (index >= 0) // if we found the vessel in our memory, use it
                {
                    vesselInfo = recentVessels[index];
                    recentVessels.RemoveAt(index); // we'll add it back the next time we switch ships
                }
                else
                {
                    vesselInfo = new VesselInfo(vessel);
                }
                
                if (recentVessels.Count >= MaxRecentVessels) 
                {
                    recentVessels.RemoveAt(0);
                }
            }
            
            List<string> toDelete = new List<string>();
            foreach (KeyValuePair<string, ResourceInfo> resourceEntry in vesselInfo.resources)
            {
                resourceEntry.Value.parts.RemoveAll(partInfo => !vessel.parts.Contains(partInfo.part));

                if (resourceEntry.Value.parts.Count == 0)
                {
                    toDelete.Add(resourceEntry.Key);
                }
            }

            foreach (string resource in toDelete)
            {
                vesselInfo.resources.Remove(resource);
            }

            Dictionary<object,int> shipIds = ComputeShipIds(vessel);
            foreach (Part part in vessel.parts)
            {
                if (part.Resources.Contains("Oxidizer") && part.Resources.Contains("LiquidFuel"))
                {
                    AddResource("_RocketFuel", part, shipIds, (p,n) => { var r = new RocketFuelResource(); r.Refresh(p); return r; });
                }
                foreach (PartResource resource in part.Resources)
                {
                    // skip the electric charge resource of engines with alternators, because they can't be balanced.
                    // any charge placed in an alternator just disappears
                    if (resource.resourceName == "ElectricCharge" && part.Modules.GetModules<ModuleAlternator>().Count != 0)
                    {
                        continue;
                    }
                    AddResource(resource.resourceName, part, shipIds, (p,n) => { var r = new SimplePartResource(n); r.Refresh(p); return r; });
                }
            }
            
            SortParts((a,b) => a.shipId - b.shipId); // make sure resources are grouped by ship ID

            vesselInfo.lastPartCount = vessel.parts.Count;
            vesselInfo.lastSituation = vessel.situation;
        }

        private void RebuildActiveVesselLists()
        {
            if (FlightGlobals.ready && FlightGlobals.fetch.activeVessel != null)
            {
                RebuildLists(FlightGlobals.fetch.activeVessel);
            }
        }
        
        private void BalanceResources(double maxFlow, List<ResourcePartMap> balanceParts)
        {
            if(balanceParts.Count < 2) return;
            
            // sort the parts by percent full and figure out what the desired percentage is
            PartResourceInfo[] resources = new PartResourceInfo[balanceParts.Count];
            double totalAmount = 0, totalCapacity = 0;
            for(int i=0; i<balanceParts.Count; i++)
            {
                resources[i]   = balanceParts[i].resource;
                totalAmount   += resources[i].Amount;
                totalCapacity += resources[i].MaxAmount;
            }
            Array.Sort(resources, (a,b) => a.PercentFull.CompareTo(b.PercentFull));
            double desiredPercentage = totalAmount / totalCapacity;

            // if the difference between the fullest and emptiest tank is small, we're done
            if(resources[resources.Length-1].PercentFull - resources[0].PercentFull < PercentEpsilon) return;
            
            // work from both sides transferring from fuller tanks (near the end) to emptier tanks (near the beginning)
            for(int di=0, si=resources.Length-1; si > di && desiredPercentage - resources[di].PercentFull >= PercentEpsilon; di++)
            {
                PartResourceInfo dest = resources[di];
                double needed = (desiredPercentage - dest.PercentFull) * dest.MaxAmount;
                for(; si > di && resources[si].PercentFull - desiredPercentage >= PercentEpsilon; si--)
                {
                    PartResourceInfo src = resources[si];
                    double available = Math.Min(maxFlow, (src.PercentFull-desiredPercentage) * src.MaxAmount);
                    needed -= src.TransferTo(dest, Math.Min(available, needed));
                    if (needed < AmountEpsilon) break; // if the dest tank became full enough, move to the next without advancing the source tank
                }
            }
        }

        private void TransferIn(double maxFlow, ResourceInfo resourceInfo, ResourcePartMap destPart)
        {
            double required = destPart.resource.MaxAmount - destPart.resource.Amount;
            if(required < AmountEpsilon) return;
            required = Math.Min(required, maxFlow);

            var srcParts = resourceInfo.parts.FindAll(
                rpm => (rpm.resource.Amount >= AmountEpsilon) &&
                       (rpm.direction == TransferDirection.NONE || rpm.direction == TransferDirection.OUT || rpm.direction == TransferDirection.DUMP ||
                        rpm.direction == TransferDirection.BALANCE));
            if(srcParts.Count == 0) return;
            double takeFromEach = required / srcParts.Count;
            foreach (ResourcePartMap srcPart in srcParts)
            {
                if (destPart.part != srcPart.part)
                {
                    srcPart.resource.TransferTo(destPart.resource, takeFromEach);
                }
            }
        }

        private void TransferOut(double maxFlow, ResourceInfo resourceInfo, ResourcePartMap srcPart)
        {
            double available = srcPart.resource.Amount;
            if(available < AmountEpsilon) return;
            available = Math.Min(available, maxFlow);
            
            var destParts = resourceInfo.parts.FindAll(
                rpm => (rpm.resource.MaxAmount - rpm.resource.Amount) >= AmountEpsilon &&
                       (rpm.direction == TransferDirection.NONE || rpm.direction == TransferDirection.IN || rpm.direction == TransferDirection.BALANCE));
            if(destParts.Count == 0) return;
            double giveToEach = available / destParts.Count;
            foreach (ResourcePartMap destPart in destParts)
            {
                if (srcPart.part != destPart.part)
                {
                    srcPart.resource.TransferTo(destPart.resource, giveToEach);
                }
            }
        }

        private void DumpOut(double maxFlow, ResourceInfo resourceInfo, ResourcePartMap partInfo)
        {
            double available = partInfo.resource.Amount;
            if(available < AmountEpsilon) return;
            partInfo.resource.SetAmount(Math.Max(0, available - maxFlow));
        }
        
        private static Dictionary<object,int> ComputeShipIds(Vessel vessel)
        {
            Dictionary<object,int> shipIds = new Dictionary<object, int>(vessel.parts.Count);
            if (vessel.parts.Count != 0)
            {
                Part rootPart = vessel.parts[0];
                while(rootPart.parent != null) rootPart = rootPart.parent;
                
                int shipId = 1;
                ComputeShipIds(shipIds, rootPart, shipId, ref shipId);
            }
            return shipIds;
        }
        
        private static void ComputeShipIds(Dictionary<object,int> shipIds, Part part, int shipId, ref int shipCounter)
        {
            shipIds[part] = shipId;
            if(part.children.Count != 0)
            {
                // if the part is a docking node, its children belong to another ship (unless the node was the root,
                // in which case there's nothing on the other side, so it's not really two ships)
                if(part.parent != null && part.Modules.GetModules<ModuleDockingNode>().Count != 0)
                {
                    shipId = ++shipCounter;
                }
                foreach (Part child in part.children)
                {
                    ComputeShipIds(shipIds, child, shipId, ref shipCounter);
                }
            }
        }
    }
}
