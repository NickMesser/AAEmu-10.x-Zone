SoundEventSpot = {
	type = "Sound",

	Properties = {
		soundName = "",
		bPlay = 0,	-- Immidiatly start playing on spawn.
		bOnce = 0,
		bEnabled = 1,
		bIgnoreCulling = 0,
		bIgnoreObstruction = 0,
	},
	
	bBlockNow=0,
	Editor={
		Model="Editor/Objects/Sound.cgf",
		Icon="Sound.bmp",
	},
}


function SoundEventSpot:OnSpawn()
	self:SetFlags(ENTITY_FLAG_CLIENT_ONLY, 0);
	
	if (System.IsEditor()) then
		Sound.Precache(self.Properties.soundName, SOUND_PRECACHE_LOAD_SOUND);
	end;
	
end


function SoundEventSpot:OnSave(save)
	--WriteToStream(stm,self.Properties);
	--System.LogToConsole("SES: OnSave:");
	save.bBlockNow = self.bBlockNow;
	save.bEnabled = self.Properties.bEnabled;
	save.bOnce = self.Properties.bOnce;
	save.bIgnoreCulling = self.Properties.bIgnoreCulling;
	save.bIgnoreObstruction = self.Properties.bIgnoreObstruction;
	
	--System.LogToConsole("bBlockNow:"..tostring(self.bBlockNow));
	--System.LogToConsole("Once:"..tostring(self.Properties.bOnce));
end

function SoundEventSpot:OnLoad(load)
	--System.LogToConsole("SES: OnLoad:");
	--self.Properties=ReadFromStream(stm);
	--self:OnReset();
	self.bBlockNow = load.bBlockNow;
	self.Properties.bEnabled = load.bEnabled;
	self.Properties.bOnce = load.bOnce;
	self.Properties.bIgnoreCulling = load.bIgnoreCulling;
	self.Properties.bIgnoreObstruction = load.bIgnoreObstruction;
	
	--System.LogToConsole("bBlockNow:"..tostring(self.bBlockNow));
	--System.LogToConsole("Once:"..tostring(self.Properties.bOnce));

	if (self.bPlay) then	
	self:Play();
	end;

end

function SoundEventSpot:OnPostSerialize()
--	System.LogToConsole("SES: PostSerial:");
	self:OnReset()
end

----------------------------------------------------------------------------------------
function SoundEventSpot:OnPropertyChange()
	-- all changes need a complete reset
	self:OnReset();
		
end
----------------------------------------------------------------------------------------
function SoundEventSpot:OnReset()
	
	-- Set basic sound params.
	--System.LogToConsole("Reset SES");
	--System.LogToConsole("self.Properties.bPlay:"..self.Properties.bPlay..", self.bBlockNow:"..self.bBlockNow);
	self.bBlockNow = 0; -- [marco] fix playonce on reset for switch editor/game mode

	--if (self.Properties.bPlay == 0 and self.soundid ~= nil) then
		self:Stop();
	--end

	if (self.Properties.bPlay ~= 0) then 
		self:Play();
	end
	--self.Client:OnMove();

end

----------------------------------------------------------------------------------------
SoundEventSpot["Server"] = {
	OnInit= function (self)
		self.bBlockNow = 0;
		self:NetPresent(0);
	end,
	OnShutDown= function (self)
	end,
}

----------------------------------------------------------------------------------------
SoundEventSpot["Client"] = {
	----------------------------------------------------------------------------------------
	OnInit = function(self)
		--System.LogToConsole("OnInit");
		self.bBlockNow = 0;
		--self.loop = self.Properties.bLoop;
		self.soundName = "";
		self.soundid = nil;
		self:NetPresent(0);

		if (self.Properties.bPlay==1) then
			self:Play();
			--System.LogToConsole("Play sound"..self.Properties.soundName);
		end
		--self.Client.OnMove(self);
	end,
	----------------------------------------------------------------------------------------
	OnShutDown = function(self)
		self:Stop();
	end,
	OnSoundDone = function(self)
	  self:ActivateOutput( "Done",true );
	  self.soundid = nil;
	  --System.LogToConsole("Done sound "..self.Properties.soundName);
	end,
}

----------------------------------------------------------------------------------------
function SoundEventSpot:Play()

	if (self.Properties.bEnabled == 0 ) then 
		do return end;
	end

  if (self.soundid ~= nil) then
		self:Stop(); -- entity proxy
	end

  if (self.bBlockNow==1) then
	do return end; -- this should only play once
  end

	--local sndFlags = bor(SOUND_EVENT, SOUND_LOOP);
	local sndFlags = SOUND_EVENT;

	sndFlags = bor(sndFlags, SOUND_START_PAUSED);
	
	if (self.Properties.bIgnoreCulling == 0) then
	  sndFlags = bor(sndFlags, SOUND_CULLING);
	end;  

	if (self.Properties.bIgnoreObstruction == 0) then
	  sndFlags = bor(sndFlags, SOUND_OBSTRUCTION);
	end;  
	
	
	--sndFlags = bor(sndFlags, SOUND_EVENT);
	--if (self.Properties.bLoop ~=0 ) then
		--sndFlags = bor(sndFlags, SOUND_LOOP);
	--end;  
  
	self.soundid = self:PlaySoundEvent(self.Properties.soundName, g_Vectors.v000, g_Vectors.v010, sndFlags, SOUND_SEMANTIC_SOUNDSPOT);
	self.soundName = self.Properties.soundName;

  if (self.soundid ~= nil) then

	local bIsEvent = (Sound.IsEvent(self.soundid));

	if (not bIsEvent) then
		System.LogToConsole( "<Sound> SoundEventSpot: ("..self:GetName()..") trys to play " ..self.soundName..". Cannot play non Events on SoundEventSpot!" );
	    	self:Stop();
	  else
		-- start the sound as because it was created paused
		Sound.SetSoundPaused(self.soundid, 0);
	  end;


	--System.LogToConsole( "Play Sound ID: "..tostring(self.soundid));
end
	
	if (self.Properties.bOnce==1) then
		self.bBlockNow = 1;
	end

	
end

----------------------------------------------------------------------------------------
function SoundEventSpot:Stop()


	--System.LogToConsole( "Stop Sound ID: "..tostring(self.soundid));
		
	if (self.soundid ~= nil) then
		self:StopSound(self.soundid); -- stopping through entity proxy
		--System.LogToConsole( "Stop Sound" );

		self.soundid = nil;
		--System.LogToConsole( "Stop Sound ID: "..tostring(self.soundid));
	end

end

----------------------------------------------------------------------------------------
function SoundEventSpot:Event_Play( sender )
	
	if (self.soundid ~= nil) then
		self:Stop();
	end
	--Log("Event_Play %d %d",self.Properties.bOnce, self.bBlockNow)

	self:Play();
	--BroadcastEvent( self,"Play" );
end

------------------------------------------------------------------------------------------------------
-- Event Handlers
------------------------------------------------------------------------------------------------------

function SoundEventSpot:Event_SoundName( sender, sSoundName )
  self.Properties.soundName = sSoundname;
  --BroadcastEvent( self,"SoundName" );
  self:OnPropertyChange();
end

function SoundEventSpot:Event_Enable( sender )
  self.Properties.bEnabled = true;
  --BroadcastEvent( self,"Enable" );
  self:OnPropertyChange();
end

function SoundEventSpot:Event_Disable( sender )
  self.Properties.bEnabled = false;
  --BroadcastEvent( self,"Disable" );
  self:OnPropertyChange();
end

function SoundEventSpot:Event_Stop( sender )
		self:Stop();
	--BroadcastEvent( self,"Stop" );
end

function SoundEventSpot:Event_Once( sender, bOnce )
	if (bOnce == true) then
		self.Properties.bOnce = 1;
	else
	  self.Properties.bOnce = 0;
	end
	--BroadcastEvent( self,"Once" );
end


SoundEventSpot.FlowEvents =
{
	Inputs =
	{
	  sound_SoundName = { SoundEventSpot.Event_SoundName, "string" },
		Enable = { SoundEventSpot.Event_Enable, "bool" },
		Disable = { SoundEventSpot.Event_Disable, "bool" },
		Play = { SoundEventSpot.Event_Play, "bool" },
		Stop = { SoundEventSpot.Event_Stop, "bool" },
		Once = { SoundEventSpot.Event_Once, "bool" },
	},
	Outputs =
	{
	  Done = "bool",
	},
}


