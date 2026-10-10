import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router'
import { api, type Me, type Settings } from './api'
import Login from './pages/Login'
import Worklist from './pages/Worklist'
import NewReport from './pages/NewReport'
import ReportEditor from './pages/ReportEditor'
import PrintReport from './pages/PrintReport'
import Templates from './pages/Templates'
import TemplateDesigner from './pages/TemplateDesigner'
import Users from './pages/Users'
import Account from './pages/Account'

type Session = { me: Me; settings: Settings; signOut: () => Promise<void> }
const SessionContext = createContext<Session | null>(null)

export function useSession(): Session {
  const s = useContext(SessionContext)
  if (!s) throw new Error('useSession outside a signed-in page')
  return s
}

export default function App() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  const location = useLocation()

  useEffect(() => {
    api.settings().then(setSettings)
    api.me().then(m => setMe(m ?? null)).catch(() => setMe(null))
  }, [])

  useEffect(() => {
    const onSignedOut = () => setMe(null)
    window.addEventListener('clinicreport:signed-out', onSignedOut)
    return () => window.removeEventListener('clinicreport:signed-out', onSignedOut)
  }, [])

  useEffect(() => {
    if (settings) document.title = settings.name
  }, [settings])

  const signOut = useCallback(async () => {
    await api.logout()
    setMe(null)
  }, [])

  if (!settings || me === undefined) return <div className="center muted">Loading…</div>
  if (!me) return <Login settings={settings} onSignedIn={setMe} />

  const printing = location.pathname.endsWith('/print')
  return (
    <SessionContext.Provider value={{ me, settings, signOut }}>
      {!printing && <TopBar />}
      {!printing && settings.demo && (
        <div className="demo-banner" role="note">
          Public demo with synthetic patients. <strong>Do not enter real patient information.</strong>
        </div>
      )}
      <main className={printing ? '' : 'page'}>
        <Routes>
          <Route path="/" element={<Worklist />} />
          <Route path="/reports/new" element={<NewReport />} />
          <Route path="/reports/:id" element={<ReportEditor />} />
          <Route path="/reports/:id/print" element={<PrintReport />} />
          <Route path="/templates" element={<Templates />} />
          <Route path="/templates/:id" element={me.role === 'Admin' ? <TemplateDesigner /> : <Navigate to="/templates" />} />
          <Route path="/users" element={me.role === 'Admin' ? <Users /> : <Navigate to="/" />} />
          <Route path="/account" element={<Account />} />
          <Route path="*" element={<Navigate to="/" />} />
        </Routes>
      </main>
    </SessionContext.Provider>
  )
}

function TopBar() {
  const { me, settings, signOut } = useSession()
  return (
    <header className="topbar">
      <div className="brand">
        <svg viewBox="0 0 32 32" width="26" height="26" aria-hidden="true">
          <rect width="32" height="32" rx="7" fill="var(--accent)" />
          <path d="M5 17h5l3-7 4 13 3-9 2 3h5" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span>{settings.name}</span>
        {settings.demo && <span className="badge warn" title="Synthetic data only">Demo</span>}
      </div>
      <nav>
        <NavLink to="/" end>Worklist</NavLink>
        <NavLink to="/templates">Templates</NavLink>
        {me.role === 'Admin' && <NavLink to="/users">Users</NavLink>}
      </nav>
      <div className="who">
        <NavLink to="/account" className="who-name">{me.displayName} <span className="muted">· {me.role}</span></NavLink>
        <button className="link" onClick={signOut}>Sign out</button>
      </div>
    </header>
  )
}
