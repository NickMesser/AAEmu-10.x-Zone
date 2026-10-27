namespace AAEmu.Zone.Simulation;

public enum ZoneRelationState : byte { None, Hostile, Neutral, Friendly }

/// <summary>Native relation state and attack permission are separate values; unit rules can override either.</summary>
public readonly record struct ZoneRelation(ZoneRelationState State, bool CanAttack, byte Reason = 0);
