AmbientVolume = {
	type = "Sound",

	Properties = {
		soundName = "",
		--iVolume=255,
		OuterRadius = 10,
		bEnabled = 1,
		bIgnoreCulling=0,
		bIgnoreObstruction=0,
		bSensitiveToBattle = 0,
		bLogBattleValue = 0,
	},
	
	started = 0,
	Editor={
		Model="Editor/Objects/Sound.cgf",
		Icon="AmbientVolume.bmp",
	},
	bEnabled = 1,
	fFade = 1,
	--bGameStarted = 0,
}


function AmbientVolume:OnSpawn()
	self:SetFlags(ENTITY_FLAG_CLIENT_ONLY, 0);
	
	--System.Log("AV: OSpawn!");
	--if (self.bGameStarted == 0) then
	--end;
	
	if (System.IsEditor()) then
		Sound.Precache(self.Properties.soundName, SOUND_PRECACHE_LOAD_SOUND);
	end;
	
end

function AmbientVolume:OnSave(save)
	--WriteToStream(stm,self.Properties);
	save.started = self.started;
	save.bEnabled = self.Properties.bEnabled;
	save.bIgnoreCulling = self.Properties.bIgnoreCulling;
	save.bIgnoreObstruction = self.Properties.bIgnoreObstruction;
	save.bSensitiveToBattle = self.Properties.bSensitiveToBattle;
	save.bLogBattleValue = self.Properties.bLogBattleValue;
	
	--System.Log("AV: ONSAVE!"..tostring(self.soundid));
	if (self.soundid ~= nil) then
	   save.fLastVolume = Sound.GetSoundVolume(self.soundid);
	   --System.Log("AV: Save LastVolume!"..tostring(save.fLastVolume));
	end;
	
end

function AmbientVolume:OnLoad(load)
	--self.Properties=ReadFromStream(stm);
	--self:OnReset();
	self:OnReset();
	
	self.started = load.started;
	self.Properties.bEnabled = load.bEnabled;
	self.Properties.bIgnoreCulling = load.bIgnoreCulling;
	self.Properties.bIgnoreObstruction = load.bIgnoreObstruction;
	self.Properties.bSensitiveToBattle = load.bSensitiveToBattle
	self.Properties.bLogBattleValue = load.bLogBattleValue


	
	if ((self.started==1) and (self.Properties.bOnce~=1)) then
    self:Play();
	end	
	
	--System.Log("AV: LastVolume!");
	if (self.soundid ~= nil) then
			--System.Log("AV: Load LastVolume!"..tostring(load.fLastVolume));
	   Sound.SetSoundVolume(self.soundid, load.fLastVolume);
	end;
	
	--System.Log("AV: ONLOAD!");

		
end

--function AmbientVolume:OnStartGame()
	--System.Log("AV: OnStartGame!");
	--self.bGameStarted = 1;
--end

----------------------------------------------------------------------------------------
function AmbientVolume:OnPropertyChange()

	--System.Log("AV: Prop change");

	if (self.soundName ~= self.Properties.soundName or self.bEnabled ~= self.Properties.bEnabled) then
		-- changes to Enable or Soundname require a reset
		--self.bEnabled = self.Properties.bEnabled;
		self:OnReset();
	end
	
	Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	--System.Log("AV: Volume changed:"..self.fFade);
	
	--if (self.volume ~= self.Properties.iVolume) then
	  --Sound.SetSoundVolume(self.soundid, self.Properties.iVolume);
	  --self.volume = self.Properties.iVolume;
	--end
	
	--if (self.radius ~= self.Properties.OuterRadius) then
	  self:SetSoundEffectRadius(self.Properties.OuterRadius);
	  --self.radius = self.Properties.OuterRadius;
	--end
	
	--if (self.bSensitive ~= self.Properties.bSensitiveToBattle) then
		--self.bSensitive = self.Properties.bSensitiveToBattle;
	--end;
	
end
----------------------------------------------------------------------------------------
function AmbientVolume:OnReset()

	
	-- Set basic sound params.
	--System.Log("Reset AV");
	--System.Log("self.Properties.bPlay:"..self.Properties.bPlay..", self.started:"..self.started);

  --self.started = 0; -- [marco] fix playonce on reset for switch editor/game mode
	--self.bEnabled = self.Properties.bEnabled;

	--if (self.Properties.bPlay == 0 and self.soundid ~= nil) then
		self:Stop();
		self:SetSoundEffectRadius(self.Properties.OuterRadius);
		--self.radius = self.Properties.OuterRadius;
		self.bEnabled = self.Properties.bEnabled;
		--System.Log("Reset AV Done"..tostring(self.bEnable));
		
		--self:Play();

	--self.started = 0; -- [marco] fix playonce on reset for switch editor/game mode
end

----------------------------------------------------------------------------------------
function AmbientVolume:UpdateBattleNoise()

    --System.Log("Updating Battle Noise...");
    
		if (self.Properties.bSensitiveToBattle == 1 and self.soundid) then
	  	local fBattle = Game.QueryBattleStatus();
	  	Sound.SetParameterValue(self.soundid, "battle", fBattle);
	  	
	  	if (self.Properties.bLogBattleValue == 1) then
	  		System.Log("AV: Current Battle Value :".. tostring(fBattle));
	  	end;
	  	
	  	self:SetTimer(1, 300); -- timer for updating battle parameter
	  	--System.Log("Updating Battle Noise :".. tostring(fBattle));
	  else
	  	self:KillTimer(1);
	  end;
end


----------------------------------------------------------------------------------------
AmbientVolume["Server"] = {
	OnInit= function (self)
		self.started = 0;
		self:NetPresent(0);
		self:SetFlags(ENTITY_FLAG_VOLUME_SOUND,0);

		--self:SetSoundEffectRadius(self.Properties.OuterRadius);
	end,
	OnShutDown= function (self)
	end,
}

----------------------------------------------------------------------------------------
AmbientVolume["Client"] = {
	----------------------------------------------------------------------------------------
	OnInit = function(self)
		--System.Log("OnInit");
		self.started = 0;
		self.inside = 0;
		self.soundName = "";
		self.soundid = nil;
		--self.bSensitive = 0;
		self:NetPresent(0);
		self:SetFlags(ENTITY_FLAG_VOLUME_SOUND,0);
		self:SetSoundEffectRadius(self.Properties.OuterRadius);
		
			--System.Log("Play sound"..self.Properties.soundName);
		end,
		--self.Client.OnMove(self);
	--end,
	----------------------------------------------------------------------------------------
	OnShutDown = function(self)
	end,
	OnEnterArea = function(self, player, nAreaID, fFade)
	  --System.Log("OnEnterArea-Client");
	  --System.Log("AV: OnEnterArea-Client - Fade: "..tostring(fFade));
		if (g_localActorId ~= player.id) then	return end;	 
		   
	  self.inside = 1;
	  if (self.soundid == nil) then
	    self:Play();
	  end;

	  if (fFade == -1) then
	     fFade = 1;
	  end;
	  
	  Sound.SetSoundVolume(self.soundid, fFade);
	  
	  if (self.fFade ~= fFade) then
	  	--self.fFade = 1;
	  	
	  	if (fFade < 0) then
	  		self.fFade = 0;
	  	else
	  		self.fFade = fFade;
	  	end;
	  	
	  	--System.Log("Fade now: "..tostring(self.fFade));
	  end;
	  
	end,
	OnProceedFadeArea = function(self, player, nAreaID, fFade)
	  --System.Log("AV: OnMoveInsideArea-Client: "..tostring(nAreaID).."-"..tostring(fFade));
	  --System.Log("AV: self-Fade now: "..tostring(self.fFade));

		if (g_localActorId ~= player.id) then	return end;	    
			  
			  
		--System.Log("Sound ID now: "..tostring(self.soundid));
		
	  if (self.soundid == nil) then
	    self:Play();
	  end;
	  
	  --System.Log("AV: selfFade: "..tostring(self.fFade));
	  --System.Log("AV: new Fade: "..tostring(fFade));
	  
	  Sound.SetSoundVolume(self.soundid, fFade);
	  
	  if (self.fFade ~= fFade) then
	  	--self.fFade = 1;
	  	
	  	if (fFade < 0) then
	  		self.fFade = 0;
	  	else
	  		self.fFade = fFade;
	  	end;
	  	
	  	--System.Log("Fade now: "..tostring(self.fFade));
	  end;
	  
	end,
	OnEnterNearArea = function(self, player, nAreaID, fFade)
	  --System.Log("OnEnterNearArea-Client");
		if (g_localActorId ~= player.id) then	return end;	    
		--System.Log("Is Camera or Player!");
		   
	  if (self.soundid == nil) then
	    self:Play();
	  end;
	end,
	OnMoveNearArea = function(self, player, nAreaID, fFade)
	  --System.Log("OnMoveNearArea-Client");
		if (g_localActorId ~= player.id) then	return end;	    
		
	  if (self.soundid == nil) then
	    self:Play();
	  end;
	end,
	OnLeaveNearArea = function(self, player, nAreaID, fFade)
	  --System.Log("OnLeaveNearArea-Client");
	  
	  --System.Log(player.class);
	  --System.Log(tostring(player.id));
	  --System.Log(tostring(g_localActorId));

	  
		if (g_localActorId ~= player.id) then	return end;	    
		
	  if (self.inside ~= 1) then
	    self:Stop();
	  end;
	end,
	OnLeaveArea = function(self, player, nAreaID, fFade)
		if (g_localActorId ~= player.id) then	return end;	    
		
	  self.inside = 0;
	  --System.Log("OLeaveArea-Client");
	  --self:Stop();
	end,
	OnTimer = function(self, timerid, msec)
		
		--System.Log("------------ AB OnTimer :"..timerid);
	
	 	if (timerid == 1) then
	 		--System.Log("------------ AB OnTimer :"..timerid);
	   	self:UpdateBattleNoise();
	   	
	 	end;
	 	
		--self:RemoveDecals();
	end,
	
	OnUnBindThis = function(self)
		System.LogToConsole("OnUnBindThis-Client");
		self:Stop();
		self.inside = 0;
	end,	
}

----------------------------------------------------------------------------------------
function AmbientVolume:Play()

  --System.Log( "...Try to Play Sound AV 1" );
  
	if (self.Properties.bEnabled == 0 ) then 
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
  
 	-- need to use *really* large max distance so a Play event does not cull the sound based on distance
 	-- we dont want the sound be rejected
	self.soundid = self:PlaySoundEventEx(self.Properties.soundName, sndFlags, 0, g_Vectors.v000, 0, 0, SOUND_SEMANTIC_AMBIENCE );
	
	-- helps to fade in the ambient without pops.
	Sound.SetFadeTime(self.soundid, 1.0, 300);
	Sound.SetSoundVolume(self.soundid, 0.0);
	--Sound.SetSoundVolume(self.soundid, 1.0*self.fFade);
	--System.Log( "AV Start to Play "..tostring(self.fFade));
	
	self:UpdateBattleNoise();
	Sound.SetSoundPaused(self.soundid, 0);
	
	self.soundName = self.Properties.soundName;
	--self.volume = self.Properties.iVolume;

	--System.Log( "----- AV Play Sound" );
	if (self.soundid ~= nil) then
  	self.started = 1;
	end;
	
	
end

----------------------------------------------------------------------------------------
function AmbientVolume:Stop()

	--System.Log( "Stop Sound AV 0" );
	
	if (self.soundid ~= nil) then
		self:StopSound(self.soundid); -- stopping through entity proxy
		
		--System.Log( "Stop Sound AV 1" );
		
		self.soundid = nil;
	end
	self.started = 0;
	self:KillTimer(1);

end


------------------------------------------------------------------------------------------------------
-- Event Handlers
------------------------------------------------------------------------------------------------------

function AmbientVolume:Event_SoundName( sender, sSoundName )
  self.Properties.soundName = sSoundName;
  --BroadcastEvent( self,"SoundName" );
  self:OnReset();
end

function AmbientVolume:Event_Deactivate(sender)
  --System.Log( "AV :FG event - Deactivate");
  self.Properties.bEnabled = 0;
  self:Stop();
end

----------------------------------------------------------------------------------------------------
function AmbientVolume:Event_Activate(sender)
	--System.Log( "AV :FG event - Active");
  self.Properties.bEnabled = 1;
  if (self.inside == 1) then
  	self:Play();
    Sound.SetSoundVolume(self.soundid, self.fFade);
  end;
  --self:OnReset();
end

function AmbientVolume:Event_Radius( sender, fRadius )
  --System.Log( "Ambient Volume :Enable radius");
	
	--self:Stop();
	--BroadcastEvent( self,"Radius" );
end

--function AmbientVolume:Event_Volume( fVolume )
	
	--self.Properties.iVolume = fVolume;
	--BroadcastEvent( self,"Volume" );
--end

AmbientVolume.FlowEvents =
{
	Inputs =
	{
	  sound_SoundName = { AmbientVolume.Event_SoundName, "string" },
		--Enable = { AmbientVolume.Event_Enable, "bool" },
		Deactivate = { AmbientVolume.Event_Deactivate, "bool" },
		Activate   = { AmbientVolume.Event_Activate, "bool" },
		Radius = { AmbientVolume.Event_Radius, "float" },
		--Volume = { AmbientVolume.Event_Volume, "float" },
	},
	Outputs =
	{
	},
}

