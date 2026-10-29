RandomSoundVolume = {
	type = "Sound",

	Properties = {
		soundName = "",
		bEnabled = 1,
		DiscRadius = 10,
		MinWaitTime = 2,
		MaxWaitTime = 5,
		--bIgnoreZAxis = 1,
		bIgnoreCulling = 0,
		bIgnoreObstruction = 0,
		bRandomPosition = 1,
		bSensitiveToBattle = 0,
		bLogBattleValue = 0,
	},
	
	started = 0,
	bEnabled = 1,
	
	Editor={
		Model="Editor/Objects/Sound.cgf",
		Icon="RandomSoundVolume.bmp",
	},
	fFade = 1,
	bEnabled = 1,
}


function RandomSoundVolume:OnSpawn()
	self:SetFlags(ENTITY_FLAG_CLIENT_ONLY, 0);
	
	if (System.IsEditor()) then
		Sound.Precache(self.Properties.soundName, SOUND_PRECACHE_LOAD_SOUND);
	end;
		
end

function RandomSoundVolume:OnSave(save)
	--WriteToStream(stm,self.Properties);
	save.started = self.started;
	save.bEnabled = self.Properties.bEnabled;
	save.bIgnoreCulling = self.Properties.bIgnoreCulling;
	save.bIgnoreObstruction = self.Properties.bIgnoreObstruction;
	save.bRandomPosition = self.Properties.bRandomPosition;
	save.bSensitiveToBattle = self.Properties.bSensitiveToBattle;
	save.bLogBattleValue = self.Properties.bLogBattleValue;
	
	--System.Log("ONSAVE - self.Properties.bEnabled:"..self.Properties.bEnabled..", self.bEnabled:"..self.bEnabled);

end

function RandomSoundVolume:OnLoad(load)
	--self.Properties=ReadFromStream(stm);
	--self:OnReset();
	self.started = load.started;
	self.Properties.bEnabled = load.bEnabled;
	self.Properties.bIgnoreCulling = load.bIgnoreCulling;
	self.Properties.bIgnoreObstruction = load.bIgnoreObstruction;
	self.Properties.bRandomPosition = load.bRandomPosition;
	self.Properties.bSensitiveToBattle = load.bSensitiveToBattle;
	self.Properties.bLogBattleValue = load.bLogBattleValue;

	self.bEnabled = self.Properties.bEnabled;
	
	--System.Log("ONLOAD - self.Properties.bEnabled:"..self.Properties.bEnabled..", self.bEnabled:"..self.bEnabled);
		
	--if ((self.started==1) and (self.Properties.bOnce~=1)) then
    --self:Play();
	--end	
	--self:Reset();
end

----------------------------------------------------------------------------------------
function RandomSoundVolume:OnPropertyChange()

	--System.Log("Prop change");
	
	if (self.soundName ~= self.Properties.soundName or self.bEnabled ~= self.Properties.bEnabled) then
		-- changes to Enable or Soundname require a reset
		--self.bEnabled = self.Properties.bEnabled;
		self:OnReset();
	end
	
	if (self.inside == 1) then
		self:SetTimer(0, self.Properties.MinWaitTime*1000);
	end;
	--self:Play();
	
	Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	
	--Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	--System.Log("RSV: Volume changed:"..self.fFade);
	
	--if (self.volume ~= self.Properties.iVolume) then
	  --Sound.SetSoundVolume(self.soundid, self.Properties.iVolume);
	  --self.volume = self.Properties.iVolume;
	--end
	
	--if (self.radius ~= self.Properties.OuterRadius) then
	 -- self:SetSoundEffectRadius(self.Properties.OuterRadius);
	 -- self.radius = self.Properties.OuterRadius;
	--end
	
	
end
----------------------------------------------------------------------------------------
function RandomSoundVolume:OnReset()

	
	-- Set basic sound params.
	--System.Log("Reset RSV");
	--System.Log("self.Properties.bEnabled:"..self.Properties.bEnabled..", self.bEnabled:"..self.bEnabled);

  --self.started = 0; -- [marco] fix playonce on reset for switch editor/game mode
	--self.bEnabled = self.Properties.bEnabled;

	--if (self.Properties.bPlay == 0 and self.soundid ~= nil) then
		self:Stop();
		--self:SetSoundEffectRadius(self.Properties.OuterRadius);
		--self.radius = self.Properties.OuterRadius;
		self.bEnabled = self.Properties.bEnabled;
		--self.bRandomPosition = self.Properties.bRandomPosition;
		
		--System.Log("New Random Pos: "..tostring(self.bRandomPosition));
		
		--self:Play();

	--self.started = 0; -- [marco] fix playonce on reset for switch editor/game mode
end


----------------------------------------------------------------------------------------
RandomSoundVolume["Server"] = {
	OnInit= function (self)
		self.started = 0;
		self:NetPresent(0);
		--self:SetFlags(ENTITY_FLAG_VOLUME_SOUND,0);

		--self:SetSoundEffectRadius(self.Properties.OuterRadius);
	end,
	OnShutDown= function (self)
	end,
}

----------------------------------------------------------------------------------------
RandomSoundVolume["Client"] = {
	----------------------------------------------------------------------------------------
	OnInit = function(self)
		--System.Log("OnInit");
		self.started = 0;
		self.inside = 0;
		self.soundName = "";
		self.soundid = nil;
		self.NextStart = self.Properties.MinWaitTime;
		self:NetPresent(0);
		--self:SetFlags(ENTITY_FLAG_VOLUME_SOUND,0);
		--self:SetSoundEffectRadius(self.Properties.OuterRadius);

			--System.Log("Play sound"..self.Properties.soundName);
		end,
		--self.Client.OnMove(self);
	--end,
	----------------------------------------------------------------------------------------
	OnShutDown = function(self)
	end,
	
	OnEnterArea = function(self, player, nAreaID, fFade)
	
	 --System.Log("RSV: OnEnterArea-Client - Fade: "..tostring(fFade));
	
		if (g_localActorId ~= player.id) then	return end;	    

	  self.inside = 1;
	  
	  if (self.bEnabled == 0) then	return end;	
	  
	  if (self.soundid == nil) then
	   	self:SetTimer(0, self.Properties.MinWaitTime*1000);
	    --self:Play();
	  end;
	end,
	
	OnTimer=function(self, timerid, msec)
	if (timerid == 0) then
	  self:Play();
	 end;
	 
	 if (timerid == 1) then
	   self:UpdateBattleNoise();
	   --self:SetTimer(1, 100); -- timer for updating battle parameter
	 end;
		--self:RemoveDecals();
	end,
	
	OnProceedFadeArea = function(self, player, nAreaID, fFade)
	  --System.Log("RSV: OnMoveInsideArea-Client: "..tostring(nParam).."-"..tostring(fParam));
	  --System.Log("RSV: self-Fade now: "..tostring(self.fFade));
		if (g_localActorId ~= player.id) then	return end;

	  if (self.fFade ~= fFade) then
	  	--self.fFade = 1;
	  	
	  	if (fFade < 0) then
	  		self.fFade = 0;
	  	else
	  		self.fFade = fFade;
	  	end;
	  	
	  	--System.Log("RSV: Fade now: "..tostring(self.fFade));
	  	self:OnPropertyChange();	  	  
	  end;
  
	end,
	--end,
	--OnEnterNearArea = function(self)
	  --System.Log("OnEnterNearArea-Client");
	  --if (self.soundid == nil) then
	   -- self:Play();
	  --end;
	--end,
	--OnMoveNearArea = function(self)
	  --System.Log("OnMoveNearArea-Client");
	  --if (self.soundid == nil) then
	    --self:Play();
	  --end;
	--end,
	--OnLeaveNearArea = function(self)
	  --System.Log("OnLeaveNearArea-Client");
	  

	--end,
	
	OnLeaveArea = function(self, player, nAreaID, fFade)
		if (g_localActorId ~= player.id) then	return end;    

	  self.inside = 0;
	  --System.Log("OLeaveArea-Client");
	  self:Stop();
	end,
	
	OnUnBindThis = function(self)
		System.LogToConsole("OnUnBindThis-Client");
		self:Stop();
		self.inside = 0;
	end,	
	
}
----------------------------------------------------------------------------------------
function RandomSoundVolume:UpdateBattleNoise()

		--System.Log("Sensitive: ".. tostring(self.Properties.bSensitiveToBattle));

		if (self.Properties.bSensitiveToBattle == 1 and self.soundid) then
	  	local fBattle = Game.QueryBattleStatus();
	  	Sound.SetParameterValue(self.soundid, "battle", fBattle);
	  	
	  	if (self.Properties.bLogBattleValue == 1) then
	  		System.Log("RSV: Current Battle Value :".. tostring(fBattle));
	  	end;
	  	
	  	--self:SetTimer(1, 150); -- timer for updating battle parameter
	  	
	  --else
	  	--self:KillTimer(1);
	  end;
end
  
----------------------------------------------------------------------------------------
function RandomSoundVolume:Play()

  --System.Log( "...Try to Play Sound RSV 1" );
  
	if (self.bEnabled == 0 ) then 
		do return end;
	end

	if (self.soundid ~= nil) then
		self:Stop();
	end
	
	local sndFlags = SOUND_DEFAULT_3D;
	
	if (self.Properties.bIgnoreCulling == 1) then
	  sndFlags = band(sndFlags, bnot(SOUND_CULLING))
	end;  

	if (self.Properties.bIgnoreObstruction == 1) then
	  sndFlags = band(sndFlags, bnot(SOUND_OBSTRUCTION))
	end;  
	
	sndFlags = bor(sndFlags, SOUND_START_PAUSED)
		
	--System.Log( "...Try to Play Sound AV 2a");
	
	local nTimeToWait;
	if(self.Properties.MinWaitTime>self.Properties.MaxWaitTime)then
		Log("MinWaitTime > MaxWaitTime for Entity '"..self:GetName().."'. Please correct values!");
		nTimeToWait=self.Properties.MinWaitTime;
	else
		nTimeToWait = math.random(self.Properties.MinWaitTime, self.Properties.MaxWaitTime);
	end;
	
	self:SetTimer(0, nTimeToWait*1000);

  --System.Log( "Random Pos: "..self.bRandomPosition);

  if (self.Properties.bRandomPosition == 1) then
  
  	if (g_localActor) then
  
			local pos=g_localActor:GetWorldPos(g_Vectors.temp_v1);

  		--local pos = self.GetWorldPos(g_Vectors.temp_v1);
		
			local rx = math.sin(math.random(0, math.pi));
			local ry = math.cos(math.random(0, math.pi));

  		pos.x = pos.x + rx*self.Properties.DiscRadius;
  		pos.y = pos.y + ry*self.Properties.DiscRadius;
  	
  		--System.Log( "...x = "..rx);

 			-- need to use *really* large max distance so a Play event does not cull the sound based on distance
 			-- we dont want the sound be rejected
			self.soundid = Sound.PlayEx(self.Properties.soundName, pos, sndFlags, self.fFade, 0, 0, SOUND_SEMANTIC_AMBIENCE_ONESHOT);
			--Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
		end;
	else
	  --System.Log( "Playing on Proxy! ");
		self.soundid = self:PlaySoundEventEx(self.Properties.soundName, sndFlags, self.fFade, g_Vectors.v000, 0, 0, SOUND_SEMANTIC_AMBIENCE_ONESHOT);
		--Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	end;
	
	--System.Log("RSV: Play vol:"..self.fFade);
	
		
		--System.Log( "...Try to Play Sound AV 2a");
		
	-- helps to fade in the ambient without pops.
	--Sound.SetFadeTime(self.soundid, 1.0, 300);
	--Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	
	self:UpdateBattleNoise();
	
	Sound.SetSoundPaused(self.soundid, 0);
	
	self.soundName = self.Properties.soundName;
	--self.volume = self.Properties.iVolume;

	--System.Log( "Play Sound ID: "..tostring(self.soundid) );
	
	if (self.soundid ~= nil) then
  	self.started = 1;
  	--System.Log( "RSV started!" );
	end;
	
	
end

----------------------------------------------------------------------------------------
function RandomSoundVolume:Stop()

	if (self.soundid ~= nil) then
			Sound.StopSound(self.soundid); -- stopping through ISound
		--System.Log( "Stop Sound" );
		self.soundid = nil;
	end
	self.started = 0;
	self:KillTimer(0);
	self:KillTimer(1);
end


------------------------------------------------------------------------------------------------------
-- Event Handlers
------------------------------------------------------------------------------------------------------

function RandomSoundVolume:Event_SoundName( sender, sSoundName )
  self.Properties.soundName = sSoundName;
  --BroadcastEvent( self,"SoundName" );
  self:OnReset();
end

--function RandomSoundVolume:Event_Enable( sender, bEnable )
  --self.Properties.bEnabled = bEnable;
  --System.Log( "Ambient Volume :Enable triggerer"..tostring(bEnable));
  --BroadcastEvent( self,"Enable" );
  --self:OnPropertyChange();
--end

function RandomSoundVolume:Event_Deactivate(sender)
  --System.Log( "Ambient Volume :Enable deactive");
  self.bEnabled = 0;
  self:Stop();

end

----------------------------------------------------------------------------------------------------
function RandomSoundVolume:Event_Activate(sender)
  --System.Log( "Ambient Volume :Enable active");
  self.bEnabled = 1;
  self:Play();
	
end

function RandomSoundVolume:Event_Radius( sender, fRadius )
  --System.Log( "Ambient Volume :Enable radius");
	self.Properties.DiscRadius = fRadius;
	self:OnPropertyChange();
	--BroadcastEvent( self,"Radius" );
end

--function RandomSoundVolume:Event_Volume( fVolume )
	
	--self.Properties.iVolume = fVolume;
	--BroadcastEvent( self,"Volume" );
--end

RandomSoundVolume.FlowEvents =
{
	Inputs =
	{
	  sound_SoundName = { RandomSoundVolume.Event_SoundName, "string" },
		--Enable = { RandomSoundVolume.Event_Enable, "bool" },
		Deactivate = { RandomSoundVolume.Event_Deactivate, "bool" },
		Activate   = { RandomSoundVolume.Event_Activate, "bool" },
		Radius = { RandomSoundVolume.Event_Radius, "float" },
		--Volume = { RandomSoundVolume.Event_Volume, "float" },
	},
	Outputs =
	{
	},
}

