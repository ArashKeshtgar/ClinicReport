import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { api, ApiError, type BillingStatus, type Meter } from '../api'

const STATUS_NOTES: Record<string, string> = {
  PastDue: 'The last payment failed. Update the card to keep the clinic running.',
  Suspended: 'This clinic is suspended: reports can be read and exported, not changed. Update the card to restore it.',
  Cancelled: 'The subscription has ended: for 30 days reports can be read and exported, not changed. Subscribe again to carry on.',
}

function Usage({ label, meter }: { label: string; meter: Meter }) {
  return <li>{label}: {meter.used}{meter.limit != null ? ` of ${meter.limit}` : ''}</li>
}

/** Hosted: the clinic's plan and what it uses of it. Subscribing and changing the card happen on Stripe's own pages. */
export default function Billing() {
  const [params] = useSearchParams()
  const [billing, setBilling] = useState<BillingStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.billing().then(setBilling).catch(e => setError(e instanceof ApiError ? e.message : 'Could not load billing.'))
  }, [])

  const goTo = async (start: () => Promise<{ url: string }>) => {
    setBusy(true)
    setError(null)
    try {
      window.location.assign((await start()).url)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not open Stripe.')
      setBusy(false)
    }
  }

  const checkout = params.get('checkout')
  return (
    <>
      <div className="page-head"><h1>Billing</h1></div>
      <div className="card pad stack narrow-card">
        {checkout === 'success' && <p className="muted" role="status">Thank you. The plan changes here as soon as Stripe confirms the payment.</p>}
        {checkout === 'cancelled' && <p className="muted" role="status">Nothing was charged.</p>}
        {billing && (
          <>
            <h2>{billing.plan} plan{billing.onTrial && ' (trial)'}</h2>
            {billing.onTrial && billing.trialEndsAt && (
              <p className="muted small">The trial ends on {new Date(billing.trialEndsAt).toLocaleDateString()}, then the Free plan's limits apply.</p>
            )}
            {STATUS_NOTES[billing.status] && <p className="error" role="alert">{STATUS_NOTES[billing.status]}</p>}
            <ul>
              <Usage label="Active users" meter={billing.users} />
              <Usage label="Services" meter={billing.services} />
              <Usage label="Reports signed this month" meter={billing.signedReports} />
            </ul>
            {!billing.configured && <p className="muted small">Billing is not set up on this server.</p>}
            {billing.configured && billing.canUpgrade && (
              <button className="primary" disabled={busy} onClick={() => goTo(api.startCheckout)}>Upgrade to Pro</button>
            )}
            {billing.configured && billing.canManage && (
              <button disabled={busy} onClick={() => goTo(api.openBillingPortal)}>Manage card and subscription</button>
            )}
          </>
        )}
        {error && <p className="error" role="alert">{error}</p>}
      </div>
    </>
  )
}
