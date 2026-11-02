import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router'
import { api, can, type Me, type Settings } from './api'
import Login, { ClinicHubSignIn } from './pages/Login'
import Signup from './pages/Signup'
import VerifyEmail, { VerifyEmailBanner } from './pages/VerifyEmail'
import Clinic from './pages/Clinic'
import Worklist from './pages/Worklist'
import NewReport from './pages/NewReport'
import ReportEditor from './pages/ReportEditor'
import PrintReport from './pages/PrintReport'
import Templates from './pages/Templates'
import TemplateDesigner from './pages/TemplateDesigner'
import Users from './pages/Users'
import Account from './pages/Account'
import Audit from './pages/Audit'
import PendingActionPage from './pages/PendingAction'
import { Logo } from './components/Logo'
import { applyTheme, savedTheme, type Theme } from './theme'

type Session = { me: Me; settings: Settings; signOut: () => Promise<void>; refreshMe: (me: Me) => void; refreshSettings: () => Promise<void> }
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
    // Connected: one sign-out, here and at ClinicHub.
    if (settings?.signIn === 'clinichub') {
      window.location.assign('/api/auth/sso/signout')
      return
    }
    await api.logout()
    setMe(null)
  }, [settings])

  if (!settings || me === undefined) return <div className="center muted">Loading…</div>
  // Hosted, on the root domain: the service's sign-up page; each clinic works at its own subdomain.
  if (settings.signup) return <Signup baseDomain={settings.baseDomain ?? window.location.host} />
  // The link from the sign-up email works before signing in.
  if (location.pathname === '/verify-email') return <VerifyEmail settings={settings} onVerified={() => api.settings().then(setSettings)} />
  if (!me) return settings.signIn === 'clinichub' ? <ClinicHubSignIn settings={settings} /> : <Login settings={settings} onSignedIn={setMe} />
  if (me.pendingAction) return <PendingActionPage me={me} settings={settings} onDone={setMe} onSignOut={signOut} />

  const printing = location.pathname.endsWith('/print')
  return (
    <SessionContext.Provider value={{ me, settings, signOut, refreshMe: setMe, refreshSettings: () => api.settings().then(setSettings) }}>
      {!printing && <TopBar />}
      {!printing && settings.demo && (
        <div className="demo-banner" role="note">
          Public demo with synthetic patients. <strong>Do not enter real patient information.</strong>
        </div>
      )}
      {!printing && settings.awaitingEmailVerification && <VerifyEmailBanner canResend={can(me, 'users.manage')} />}
      <main className={printing ? '' : 'page'}>
        <Routes>
          <Route path="/" element={<Worklist />} />
          <Route path="/reports/new" element={<NewReport />} />
          <Route path="/reports/:id" element={<ReportEditor />} />
          <Route path="/reports/:id/print" element={<PrintReport />} />
          <Route path="/templates" element={<Templates />} />
          <Route path="/templates/:id" element={can(me, 'templates.design') ? <TemplateDesigner /> : <Navigate to="/templates" />} />
          <Route path="/users" element={can(me, 'users.manage') ? <Users /> : <Navigate to="/" />} />
          <Route path="/audit" element={can(me, 'audit.view') ? <Audit /> : <Navigate to="/" />} />
          <Route path="/account" element={<Account />} />
          <Route path="/clinic" element={settings.hosted && can(me, 'users.manage') ? <Clinic /> : <Navigate to="/" />} />
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
        <Logo />
        <span>{settings.name}</span>
        {me.clinic && <span className="badge" title="Signed in through ClinicHub">{me.clinic}</span>}
        {settings.demo && <span className="badge warn" title="Synthetic data only">Demo</span>}
      </div>
      <nav>
        <NavLink to="/" end>Worklist</NavLink>
        <NavLink to="/templates">Templates</NavLink>
        {can(me, 'users.manage') && <NavLink to="/users">{settings.signIn === 'clinichub' ? 'Users' : <>Users &amp; roles</>}</NavLink>}
        {can(me, 'audit.view') && <NavLink to="/audit">Audit log</NavLink>}
        {settings.hosted && can(me, 'users.manage') && <NavLink to="/clinic">Clinic</NavLink>}
      </nav>
      <div className="who">
        <ThemeToggle />
        <NavLink to="/account" className="who-name">{me.displayName} <span className="muted">· {me.roles.join(', ')}</span></NavLink>
        <button className="link" onClick={signOut}>Sign out</button>
      </div>
    </header>
  )
}

function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(savedTheme())
  const next: Theme = theme === 'dark' ? 'light' : 'dark'
  return (
    <button className="theme-toggle" title={`Switch to ${next} theme`} aria-label={`Switch to ${next} theme`}
      onClick={() => { applyTheme(next); setTheme(next) }}>
      {theme === 'dark' ? '☀' : '☾'}
    </button>
  )
}
