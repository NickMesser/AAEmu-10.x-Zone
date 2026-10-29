--------------------------------------------------
--   Created By: <Fren>
--   Description: <return_state>
--------------------------------------------------

AIBehaviour.return_state = {
	Name = "return_state",
    alertness = AIALERTNESS_IDLE,

	--------------------------------------------------------------------------
	Constructor = function(self, entity)
        entity.AI.prevAutoDisable = AI.GetAutoDisable(entity.id);
        if (entity.AI.prevAutoDisable == true) then
            AI.AutoDisable(entity.id, 0);
        end

        entity:ClearAICombat();
        entity.unit:NpcActivateAggroUpdate(false);

        if (entity.AI.param.clearSCtrlWhenClearCombat == true) then
          entity.unit:ClearSkillController();
        end

        local alertness = AI.GetAlertness(entity.id);
        entity.AI.needRestorationOnReturn = (alertness ~= nil and alertness >= AIALERTNESS_COMBAT) and
                                ((entity.AI.param.restorationOnReturn == nil) or (entity.AI.param.restorationOnReturn == true));

        if (entity.AI.needRestorationOnReturn) then
            entity.unit:StartSkill(entity.unit, NPC_RETURN_SKILL_TYPE, true);
            entity.unit:CreateBuff(NPC_RETURN_BUFF_TYPE);
        end

        if (entity.AI.param.alwaysTeleportOnReturn) then
            self:OnCompletedReturn(entity);
            return;
        end
        
        if (entity.AI.param.goReturnState ~= nil and entity.AI.param.goReturnState == false) then
            self:OnCompletedReturnNoTeleport(entity);
            return;
        end

        
		AI.BeginGoalPipe("return_state");
            AI.PushGoal("locate", 1, "refpoint");
            AI.PushGoal("run", 1, 1);
            AI.PushGoal("approach", 1, 2, AILASTOPRES_USE, -1);
            AI.PushGoal("signal", 1, AISIGNAL_INCLUDE_DISABLED, "OnCompletedReturn", SIGNALFILTER_SENDER);
        AI.EndGoalPipe();

		entity:SelectPipe(AIGOALPIPE_DONT_RESET_AG, "return_state");
        entity.unit:NpcActivateAITimeEvent(true, 1);
        
        entity.AI.move_timeoutCount = 20;   -- For return, timeout is 20 sec
	end,

    --------------------------------------------------------------------------
    Destructor = function(self, entity)
        entity.AI.move_timeoutCount = nil;

        entity.unit:NpcActivateAITimeEvent(false, 0);
        entity.unit:NpcActivateAggroUpdate(true);
        entity:ClearAICombat();

        if (entity.AI.needRestorationOnReturn) then
            entity.unit:RemoveBuff(NPC_RETURN_BUFF_TYPE);
        end
        entity.AI.needRestorationOnReturn = nil;

        if (entity.AI.prevAutoDisable == true) then
            AI.AutoDisable(entity.id, 1);
        end
        entity.AI.prevAutoDisable = nil;
    end,

	--------------------------------------------------------------------------
    OnAITimeEvent = function(self, entity, sender, data)
        entity.AI.move_timeoutCount = entity.AI.move_timeoutCount - 1;
        if (entity.AI.move_timeoutCount <= 0) then
            local returnPos = AI.GetRefPointPosition(entity.id);
            entity.unit:NpcTeleportTo(returnPos);
            self:OnCompletedReturn(entity, sender, data);
        end
    end,

	--------------------------------------------------------------------------
    OnPathStuck = function(self, entity, sender, data)
        entity.AI.move_timeoutCount = 0;
        self:OnAITimeEvent(entity, sender, data);
    end,

	--------------------------------------------------------------------------
    OnCompletedReturn = function(self, entity, sender, data)
        local returnPos = AI.GetRefPointPosition(entity.id);
        local entityPos = entity:GetPos();
        if (DistanceSqVectors(returnPos, entityPos) > 2 * 2) then
            entity.unit:NpcTeleportTo(returnPos);
        end

        if (entity.AI.returnSignal ~= nil) then
            AI.Signal(SIGNALFILTER_SENDER, AISIGNAL_INCLUDE_DISABLED, entity.AI.returnSignal, entity.id);
            entity.AI.returnSignal = nil;
        else
            AI.Signal(SIGNALFILTER_SENDER, AISIGNAL_INCLUDE_DISABLED, "GO_TO_RUN_COMMAND_SET", entity.id);
        end
    end,
    
  	--------------------------------------------------------------------------
    OnCompletedReturnNoTeleport = function(self, entity, sender, data)
        if (entity.AI.returnSignal ~= nil) then
            AI.Signal(SIGNALFILTER_SENDER, AISIGNAL_INCLUDE_DISABLED, entity.AI.returnSignal, entity.id);
            entity.AI.returnSignal = nil;
        else
            AI.Signal(SIGNALFILTER_SENDER, AISIGNAL_INCLUDE_DISABLED, "GO_TO_RUN_COMMAND_SET", entity.id);
        end
    end,

	--------------------------------------------------------------------------
    -- Ignore almost events
    OnEnemySeen = function(self, entity)
    end,

    OnFriendSeen = function(self, entity)
    end,

    OnGroupLeaderDied = function(self, entity)
    end,

    OnGroupMemberDied = function(self, entity)
    end,
}
