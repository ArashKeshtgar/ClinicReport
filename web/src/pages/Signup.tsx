import { useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, type SignupResult, type SlugCheck } from '../api'
import { Logo } from '../components/Logo'

/** A clinic's address from its name: "St. Mary's Heart Clinic" → "st-marys-heart-clinic". */
export function suggestSlug(name: string): string {
  return name.normalize('NFKD').replace(/[̀-ͯ]/g, '').toLowerCase()
    .replace(/['’]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40).replace(/-+$/, '')
}

/** Hosted mode, on the root domain: a clinic signs itself up and gets {slug}.{baseDomain}. */
export default function Signup({ baseDomain }: { baseDomain: string }) {
  const [clinicName, setClinicName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)
  const [check, setCheck] = useState<SlugCheck | null>(null)
  const [adminName, setAdminName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<SignupResult | null>(null)

  const address = slugEdited ? slug : suggestSlug(clinicName)

  // Is the address free? Asked a moment after typing stops.
  useEffect(() => {
    if (address.length < 2) { setCheck(null); return }
    const timer = setTimeout(() => api.checkSlug(address).then(setCheck).catch(() => setCheck(null)), 300)
    return () => clearTimeout(timer)
  }, [address])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setErrors({})
    try {
      setDone(await api.signup({ clinicName, slug: address, adminName, email, password }))
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length > 0) setErrors(e.fieldErrors)
      else setError(e instanceof ApiError ? e.message : 'Could not sign up.')
    } finally {
      setBusy(false)
    }
  }

  const fieldError = (name: string) => errors[name]?.[0] && <span className="error small">{errors[name][0]}</span>

  if (done) {
    return (
      <div className="login">
        <div className="login-card card stack">
          <div className="login-brand"><Logo size={40} /><h1>Your clinic is ready</h1></div>
          <p>{clinicName} is at <a href={done.url}>{done.url}</a>. Sign in there with <strong>{email}</strong> and the
            password you chose.</p>
          <p>{done.emailSent
            ? <>We sent a link to {email}. Open it to confirm the address; reports can be started once it is confirmed.</>
            : <>The confirmation email could not be sent. After signing in, use “Send a new link” at the top of the page.</>}</p>
          <p className="muted small">Free trial until {new Date(done.trialEndsAt).toLocaleDateString()}.</p>
          <a className="button primary" href={done.url}>Go to {clinicName}</a>
        </div>
      </div>
    )
  }

  return (
    <div className="login">
      <div className="login-card card">
        <div className="login-brand"><Logo size={40} /><h1>ClinicReport</h1></div>
        <p className="muted">Structured clinical reporting for your clinic. Sign up for a 14-day trial.</p>
        <form onSubmit={submit} className="stack">
          <label>Clinic name
            <input value={clinicName} onChange={e => setClinicName(e.target.value)} autoFocus required maxLength={200} />
            {fieldError('clinicName')}
          </label>
          <label>Address
            <span className="slug-input">
              <input value={address} required maxLength={40} aria-describedby="slug-status"
                onChange={e => { setSlugEdited(true); setSlug(e.target.value.toLowerCase()) }} />
              <span className="muted">.{baseDomain}</span>
            </span>
            <span id="slug-status" className="small">
              {errors.slug ? <span className="error">{errors.slug[0]}</span>
                : check?.slug === address && (check.available ? <span className="muted">Available</span> : <span className="error">{check.problem}</span>)}
            </span>
          </label>
          <label>Your name
            <input value={adminName} onChange={e => setAdminName(e.target.value)} autoComplete="name" required maxLength={128} />
            {fieldError('adminName')}
          </label>
          <label>Email
            <input type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="email" required maxLength={64} />
            <span className="muted small">You sign in with it, as the clinic's administrator.</span>
            {fieldError('email')}
          </label>
          <label>Password
            <input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password" required minLength={10} />
            {fieldError('password')}
          </label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="primary" disabled={busy || (check?.slug === address && !check.available)}>Create my clinic</button>
        </form>
      </div>
      <p className="muted small disclaimer">
        A portfolio service in test mode. Do not enter real patient information. ClinicReport is not a certified
        medical device.
      </p>
    </div>
  )
}
