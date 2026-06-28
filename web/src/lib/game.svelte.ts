import * as signalR from '@microsoft/signalr'

const CONTRACT_VERSION = 1
const LS_KEY = 'lantern.session'

export type RosterEntry = { playerId: string; displayName: string; isHost: boolean; connected: boolean }

type Session = { roomCode: string; playerId: string; playerToken: string }

// Reactive game client (Svelte 5 runes). One per app. Connects to the same-origin /hub/v1
// (Vite proxies to the server in dev; same origin in prod), so the URL is always relative.
class GameClient {
  connected = $state(false)
  state = $state<any>(null) // ShowdownState | null
  roster = $state<RosterEntry[]>([])
  seq = $state(-1)
  error = $state<string | null>(null)
  session = $state<Session | null>(null)

  private conn: signalR.HubConnection | null = null
  private cid = 0

  get isHost(): boolean {
    return this.roster.find((p) => p.playerId === this.session?.playerId)?.isHost ?? false
  }

  private async ensureConnection(): Promise<signalR.HubConnection> {
    if (this.conn) return this.conn
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub/v1')
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 15000, 30000])
      .build()

    conn.on('Snapshot', (m) => this.applySnapshot(m))
    conn.on('Presence', (m) => { this.roster = m.payload.players })
    conn.on('RoomError', (m) => { this.error = `${m.payload.code}: ${m.payload.message}` })
    conn.onreconnected(() => this.resume())
    conn.onclose(() => { this.connected = false })

    await conn.start()
    this.connected = true
    this.conn = conn
    return conn
  }

  private applySnapshot(m: any) {
    if (m.seq < this.seq) return
    this.seq = m.seq
    this.state = m.payload.state
    this.roster = m.payload.roster.players
  }

  private save() {
    if (this.session) localStorage.setItem(LS_KEY, JSON.stringify(this.session))
  }

  loadSession(): Session | null {
    const raw = localStorage.getItem(LS_KEY)
    return raw ? (JSON.parse(raw) as Session) : null
  }

  async createRoom(hostPassword: string, displayName: string, contentPackId: string, roomPassword?: string) {
    this.error = null
    const conn = await this.ensureConnection()
    try {
      const r = await conn.invoke('CreateRoom', { hostPassword, displayName, contentPackId, roomPassword: roomPassword || null, contractVersion: CONTRACT_VERSION })
      this.session = { roomCode: r.roomCode, playerId: r.playerId, playerToken: r.playerToken }
      this.applySnapshot(r.snapshot)
      this.save()
    } catch (e: any) { this.error = String(e?.message ?? e) }
  }

  async join(roomCode: string, displayName: string, roomPassword?: string) {
    this.error = null
    const conn = await this.ensureConnection()
    try {
      const r = await conn.invoke('JoinRoom', { roomCode: roomCode.toUpperCase(), displayName, roomPassword: roomPassword || null, contractVersion: CONTRACT_VERSION })
      this.session = { roomCode: r.roomCode, playerId: r.playerId, playerToken: r.playerToken }
      this.applySnapshot(r.snapshot)
      this.save()
    } catch (e: any) { this.error = String(e?.message ?? e) }
  }

  async resume() {
    const s = this.session ?? this.loadSession()
    if (!s) return
    this.error = null
    const conn = await this.ensureConnection()
    try {
      const r = await conn.invoke('Resume', { roomCode: s.roomCode, playerId: s.playerId, playerToken: s.playerToken, contractVersion: CONTRACT_VERSION })
      this.session = s
      this.applySnapshot(r.snapshot)
      this.save()
    } catch (e: any) { this.error = `resume failed: ${String(e?.message ?? e)}`; this.leave() }
  }

  async submit(intent: Record<string, unknown>) {
    if (!this.conn || !this.session) return
    this.error = null
    try {
      const ack = await this.conn.invoke('SubmitIntent', {
        roomCode: this.session.roomCode, playerId: this.session.playerId, playerToken: this.session.playerToken,
        clientIntentId: `${this.session.playerId}-${++this.cid}`, intent,
      })
      if (!ack.accepted) this.error = `rejected: ${ack.rejectReason}`
    } catch (e: any) { this.error = String(e?.message ?? e) }
  }

  leave() {
    localStorage.removeItem(LS_KEY)
    this.session = null
    this.state = null
    this.roster = []
    this.seq = -1
  }
}

export const game = new GameClient()
