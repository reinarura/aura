//--- Aura Script -----------------------------------------------------------
//  Zombie AI
//--- Description -----------------------------------------------------------
//  AI for Zombie type monster.
//--- History ---------------------------------------------------------------
// 1.0 Added general AI behaviors
// Missing: aggro over time, visual angle
//---------------------------------------------------------------------------

[AiScript("zombie")]
public class ZombieAi : AiScript
{
	readonly string[] ZombieChat = new[] 
	{
		L("Ahh..."),
		L("Woo woo woo...."),
		L("Woo woo..."),
		L("Woo woo"),
		L("Woo..."),
		L("Umph...."),
		L("Aww aww"),
		L("Woo woo woo...."),
		L("Gurgle!"),
		L("Ah woo!"),
		L("Kuh"),
		L("Umph..."),
	};

	public ZombieAi()
	{
		SetAggroRadius(1500); // audio 1200 visual Angle 90°
		Hates("/pc/", "/pet/");
		Hates("/ahchemy_golem/");

		On(AiState.Aggro, AiEvent.DefenseHit, OnDefenseHit);
		On(AiState.Aggro, AiEvent.Hit, OnHit);
		On(AiState.Aggro, AiEvent.KnockDown, OnKnockDown);
	}

	protected override IEnumerable Idle()
	{
		if (Creature.Skills.Has(SkillId.Rest))
			Do(StartSkill(SkillId.Rest));
		Do(Wait(1000000000));
	}

	protected override IEnumerable Aggro()
	{
		var rndnum = Random();
		if (rndnum < 30) // 30%
		{
			if (Random() < 30)
				Do(Say(ZombieChat));
			Do(Attack(5, 10000));
		}
		else if (rndnum < 80) // 50%
		{
			if (Random() < 60)
				Do(Say(ZombieChat));
			Do(PrepareSkill(SkillId.Counterattack));
			Do(Wait(2000, 15000));
			Do(CancelSkill());
		}
		else // 20%
		{
			if (Random() < 30)
				Do(Say(ZombieChat));
			Follow(600, true, 5000);
		}
	}

	private IEnumerable OnDefenseHit()
	{
		Do(Attack());
		Do(Wait(3000));
	}

	private IEnumerable OnHit()
	{
		if (Random() < 80)
			Do(Attack(5, 10000));
	}

	private IEnumerable OnKnockDown()
	{
		if (Random() < 60)
			Do(Say(ZombieChat));
		var rndOKD = Random();
		if (rndOKD < 60) // 60%
		{
			Do(PrepareSkill(SkillId.Counterattack));
			Do(Wait(4000, 10000));
			Do(CancelSkill());
		}
		else // 40%
		{
			Do(Attack(5, 4000));
		}
	}
}
