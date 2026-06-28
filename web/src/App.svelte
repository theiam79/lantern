<script lang="ts">
  import { onMount } from 'svelte'
  import { game } from './lib/game.svelte'

  // lobby form state
  let hostPassword = $state('')
  let displayName = $state('')
  let contentPackId = $state('sample')
  let roomPassword = $state('')
  let joinCode = $state('')

  // showdown form state
  let monsterId = $state('sample-beast')
  let monsterLevelId = $state('prologue')
  let newSurvivorName = $state('')
  let newSurvivorWeapon = $state('training-blade')
  let attackSurvivorId = $state('')
  let attackWeaponId = $state('training-blade')
  let hitCount = $state(0)
  let monsterNote = $state('')

  onMount(() => { if (game.loadSession()) game.resume() })

  const s = $derived(game.state)
  const survivors = $derived(s ? (Object.values(s.survivors) as any[]) : [])

  const startShowdown = () => game.submit({ type: 'StartShowdown', monsterId, monsterLevelId, masterSeed: null })
  function addSurvivor() {
    if (!newSurvivorName) return
    game.submit({
      type: 'AddSurvivor', survivorId: crypto.randomUUID().slice(0, 8), name: newSurvivorName,
      attributes: { mov: 5, acc: 0, str: 0, eva: 0, lck: 0, spd: 0 }, weaponId: newSurvivorWeapon,
    })
    newSurvivorName = ''
  }
  const declareAttack = () => attackSurvivorId &&
    game.submit({ type: 'DeclareAttack', survivorId: attackSurvivorId, weaponId: attackWeaponId, targetMonsterId: s.monster.id })
  function enterHits() { game.submit({ type: 'EnterHits', attackId: s.attack.attackId, mode: 'count', rolls: null, hitCount }); hitCount = 0 }
  const drawLocations = () => game.submit({ type: 'DrawHitLocations', attackId: s.attack.attackId })
  const enterWound = (cardId: string, outcome: string) =>
    game.submit({ type: 'EnterWound', attackId: s.attack.attackId, locationCardId: cardId, mode: 'outcome', roll: null, outcome })
  const applyAttack = () => game.submit({ type: 'ApplyAttackResult', attackId: s.attack.attackId })
  function recordMonster() { game.submit({ type: 'RecordMonsterTurn', note: monsterNote, survivorWound: null }); monsterNote = '' }
  const undo = () => game.submit({ type: 'Undo', count: 1, targetSeq: null })
  const endShowdown = () => game.submit({ type: 'EndShowdown', result: 'Abort' })
</script>

<main>
  <h1>🏮 Lantern</h1>
  {#if game.error}<p class="error">{game.error}</p>{/if}

  {#if !game.session}
    <div class="cols">
      <section class="card">
        <h2>Host a room</h2>
        <label>Host password <input type="password" bind:value={hostPassword} /></label>
        <label>Your name <input bind:value={displayName} /></label>
        <label>Content pack <input bind:value={contentPackId} /></label>
        <label>Room password (optional) <input type="password" bind:value={roomPassword} /></label>
        <button onclick={() => game.createRoom(hostPassword, displayName, contentPackId, roomPassword)}>Create room</button>
      </section>
      <section class="card">
        <h2>Join a room</h2>
        <label>Room code <input bind:value={joinCode} style="text-transform:uppercase" /></label>
        <label>Your name <input bind:value={displayName} /></label>
        <label>Room password (if any) <input type="password" bind:value={roomPassword} /></label>
        <button onclick={() => game.join(joinCode, displayName, roomPassword)}>Join</button>
      </section>
    </div>
  {:else}
    <header class="bar">
      <strong>Room {game.session.roomCode}</strong>
      <span class="dot" class:on={game.connected}></span>{game.connected ? 'connected' : 'offline'}
      <span class="spacer"></span>
      <button onclick={() => game.leave()}>Leave</button>
    </header>

    <div class="roster">
      {#each game.roster as p (p.playerId)}
        <span class="player" class:off={!p.connected}>{p.displayName}{p.isHost ? ' 👑' : ''}</span>
      {/each}
    </div>

    {#if !s}
      <section class="card">
        <h2>Start showdown</h2>
        <label>Monster <input bind:value={monsterId} /></label>
        <label>Level <input bind:value={monsterLevelId} /></label>
        <button onclick={startShowdown}>Start</button>
      </section>
    {:else}
      <section class="card monster">
        <h2>{s.monster.id} — <span class="status">{s.status}</span></h2>
        <p>Wounds <strong>{s.monster.totalWounds}</strong> / {s.monster.toughnessWoundThreshold}
          · Deck {s.deck.drawPile.length}↓ / {s.deck.discardPile.length}🗑</p>
      </section>

      <section class="card">
        <h3>Survivors</h3>
        <ul>{#each survivors as sv (sv.survivorId)}<li>{sv.name} ({sv.weaponId}){sv.dead ? ' ☠️' : ''}</li>{/each}</ul>
        <div class="row">
          <input placeholder="new survivor name" bind:value={newSurvivorName} />
          <input placeholder="weapon id" bind:value={newSurvivorWeapon} />
          <button onclick={addSurvivor}>Add survivor</button>
        </div>
      </section>

      {#if s.status === 'Idle'}
        <section class="card">
          <h3>Declare attack</h3>
          <div class="row">
            <select bind:value={attackSurvivorId}>
              <option value="" disabled>pick survivor</option>
              {#each survivors as sv (sv.survivorId)}<option value={sv.survivorId}>{sv.name}</option>{/each}
            </select>
            <input bind:value={attackWeaponId} />
            <button onclick={declareAttack} disabled={!attackSurvivorId}>Attack</button>
          </div>
        </section>
      {:else if s.attack}
        <section class="card attack">
          <h3>Attack — hits on {s.attack.hitsOn}+ ({s.attack.attackDice} dice)</h3>
          {#if s.status === 'AwaitingHits'}
            <p>Roll {s.attack.attackDice} dice; each ≥ {s.attack.hitsOn} is a hit.</p>
            <div class="row">
              <input type="number" min="0" max={s.attack.attackDice} bind:value={hitCount} />
              <button onclick={enterHits}>Enter hits</button>
            </div>
          {:else if s.status === 'DrawingLocations'}
            <button onclick={drawLocations}>Draw {s.attack.enteredHitCount} hit location(s)</button>
          {:else if s.status === 'AwaitingWounds'}
            <p>Wounds on {s.attack.drawnLocations[0]?.woundsOn}+ — roll per location:</p>
            <ul>
              {#each s.attack.drawnLocations as loc, i (i)}
                <li>
                  {loc.cardId}{loc.hasCriticalSlot ? ' ✦' : ''}{loc.isTrap ? ' ⚠️ trap' : ''}
                  {#if loc.result}<strong> → {loc.result}</strong>
                  {:else if !loc.isTrap}
                    <button onclick={() => enterWound(loc.cardId, 'wound')}>Wound</button>
                    <button onclick={() => enterWound(loc.cardId, 'fail')}>Fail</button>
                    <button onclick={() => enterWound(loc.cardId, 'crit')}>Crit</button>
                  {/if}
                </li>
              {/each}
            </ul>
          {:else if s.status === 'Resolved'}
            <button onclick={applyAttack}>Apply attack result</button>
          {/if}
        </section>
      {/if}

      <section class="card">
        <h3>Monster turn (manual)</h3>
        <div class="row">
          <input placeholder="what did the monster do?" bind:value={monsterNote} />
          <button onclick={recordMonster}>Record</button>
        </div>
        {#if s.monsterTurnNote}<p class="note">Last: {s.monsterTurnNote}</p>{/if}
      </section>

      {#if game.isHost}
        <section class="card host">
          <h3>Host controls</h3>
          <button onclick={undo}>Undo last</button>
          <button onclick={endShowdown}>End showdown</button>
        </section>
      {/if}
    {/if}
  {/if}
</main>
