import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { clientId, type Report } from './api'

export type Viewer = { connectionId: string; userId: number; name: string; role: string; fieldId: string | null }

/**
 * Joins the report's live channel: other people's saved changes arrive through `onRemoteChange`, and
 * `viewers` says who else has the report open and which field they are in.
 */
export function useReportLive(reportId: number, onRemoteChange: (r: Report) => void, onReconnected: () => void) {
  const [viewers, setViewers] = useState<Viewer[]>([])
  const [connected, setConnected] = useState(false)
  const connection = useRef<HubConnection | null>(null)
  const handlers = useRef({ onRemoteChange, onReconnected })
  handlers.current = { onRemoteChange, onReconnected }

  useEffect(() => {
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/report')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()
    connection.current = conn

    conn.on('reportChanged', (msg: { clientId: string | null; report: Report }) => {
      if (msg.clientId !== clientId) handlers.current.onRemoteChange(msg.report)
    })
    conn.on('presence', (v: Viewer[]) => setViewers(v))
    let cancelled = false
    conn.onreconnecting(() => setConnected(false))
    conn.onreconnected(async () => {
      await conn.invoke('JoinReport', reportId)
      setConnected(true)
      handlers.current.onReconnected() // we may have missed changes while offline
    })
    // Guard every callback: React StrictMode (and fast navigation) stop a connection that is still starting.
    conn.onclose(() => { if (!cancelled) setConnected(false) })

    conn.start()
      .then(() => cancelled ? undefined : conn.invoke('JoinReport', reportId))
      .then(() => { if (!cancelled) setConnected(true) })
      .catch(() => { if (!cancelled) setConnected(false) })

    return () => {
      cancelled = true
      conn.stop()
    }
  }, [reportId])

  const focus = (fieldId: string | null) => {
    const conn = connection.current
    if (conn?.state === HubConnectionState.Connected) conn.invoke('Focus', fieldId).catch(() => {})
  }

  return { viewers, connected, focus, myConnectionId: () => connection.current?.connectionId ?? null }
}
