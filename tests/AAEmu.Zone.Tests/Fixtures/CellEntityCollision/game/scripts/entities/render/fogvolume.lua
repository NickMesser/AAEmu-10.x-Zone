FogVolume = {
  type = "FogVolume",
	--ENTITY_DETAIL_ID=1,

	Properties = {
		bActive = 1,
		
		VolumeType = 0,						
		Size = { x = 1, y = 1, z = 1 },		
		color_Color = { x = 1, y = 1, z = 1 },
		fHDRDynamic = 0,		-- -1=darker..0=normal..1=brighter
		bUseGlobalFogColor = 0,
		
		GlobalDensity = 1.0,		
		FallOffDirLong = 0.0,
		FallOffDirLati = 90.0,
		FallOffShift = 0.0,
		FallOffScale = 1.0,		
		SoftEdges = 1.0,
	},
	
	Fader = {
		fadeTime = 0.0,
		fadeToValue = 0.0,
	},

	Editor = {
		Model = "Editor/Objects/invisiblebox.cgf",
		Icon = "FogVolume.bmp",
		ShowBounds = 1,
	},
}


function FogVolume:OnSpawn()
	self:SetFlags(ENTITY_FLAG_CLIENT_ONLY, 0);
end

-------------------------------------------------------
function FogVolume:InitFogVolumeProperties()
  --System.Log( "FogVolume:InitFogVolumeProperties" )
	local props = self.Properties;	
	self:LoadFogVolume( 0, self.Properties );
end;

-------------------------------------------------------
function FogVolume:CreateFogVolume()
	--System.Log( "FogVolume:CreateFogVolume" )
	self:InitFogVolumeProperties()
end

-------------------------------------------------------
function FogVolume:DeleteFogVolume()
	--System.Log( "FogVolume:DeleteFogVolume" )
	self:FreeSlot( 0 );
end

-------------------------------------------------------
function FogVolume:OnInit()
  if( self.Properties.bActive == 1 ) then
		self:CreateFogVolume();
	end
end

-------------------------------------------------------
function FogVolume:CheckMove()
end

-------------------------------------------------------
function FogVolume:OnShutDown()
end

-------------------------------------------------------
function FogVolume:OnPropertyChange()
  --System.Log( "FogVolume:OnPropertyChange" )
  if( self.Properties.bActive == 1 ) then
		self:CreateFogVolume();
	else
		self:DeleteFogVolume();
	end
end

-------------------------------------------------------
function FogVolume:OnReset()
  if( self.Properties.bActive == 1 ) then
		self:CreateFogVolume();
	end
end

-------------------------------------------------------
-- Hide Event
-------------------------------------------------------
function FogVolume:Event_Hide()
	self:DeleteFogVolume();
end

-------------------------------------------------------
-- Show Event
-------------------------------------------------------
function FogVolume:Event_Show()	
	self:CreateFogVolume();
end

-------------------------------------------------------
-- Fade Event
-------------------------------------------------------
function FogVolume:Event_Fade()	
	--System.Log("Do Fading");
	self:FadeGlobalDensity(0, self.Fader.fadeTime, self.Fader.fadeToValue);
end

-------------------------------------------------------
-- Fade Time Event
-------------------------------------------------------
function FogVolume:Event_FadeTime(i, time)	
	--System.Log("Fade time "..tostring(time));
	self.Fader.fadeTime = time;
end

-------------------------------------------------------
-- Event_GlobalDensity
-------------------------------------------------------
function FogVolume:Event_SetGlobalDensity(i, val)
	self:SetGlobalDensity(0, val);
    self.GlobalDensity = val;
end

-------------------------------------------------------
-- Fade Value Event
-------------------------------------------------------
function FogVolume:Event_FadeValue(i, val)	
	--System.Log("Fade val "..tostring(val));
	self.Fader.fadeToValue = val;
end


FogVolume.FlowEvents =
{
	Inputs =
	{
		Hide  = { FogVolume.Event_Hide, "bool" },
		Show  = { FogVolume.Event_Show, "bool" },
		x_Time  = { FogVolume.Event_FadeTime, "float" },
		y_Value = { FogVolume.Event_FadeValue, "float" },
		z_Fade  = { FogVolume.Event_Fade, "bool" },
        SetGlobalDensity = { FogVolume.Event_SetGlobalDensity, "float" },
	},
	Outputs =
	{
		Hide = "bool",
		Show = "bool",
	},
}
