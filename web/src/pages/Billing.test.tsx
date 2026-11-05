import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Billing, { BillingBanner } from './Billing'

const status = (over: object = {}) => ({
  plan: 'Free', status: 'Active', onTrial: false, trialEndsAt: null,
  users: { used: 2, limit: 3 }, services: { used: 2, limit: 2 }, signedReports: { used: 7, limit: 50 },
  configured: true, canUpgrade: true, canManage: false, ...over,
})

const show = (url = '/billing') => render(<MemoryRouter initialEntries={[url]}><Billing /></MemoryRouter>)

describe('Billing', () => {
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

  it('shows the plan and its limits, and goes to Stripe to upgrade', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async input =>
      new Response(JSON.stringify(String(input).endsWith('/checkout') ? { url: 'https://checkout.stripe.test/s' } : status())))
    show()

    expect(await screen.findByRole('heading', { name: 'Free plan' })).toBeInTheDocument()
    expect(screen.getByText('Reports signed this month: 7 of 50')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Manage/ })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Upgrade to Pro' }))
    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('https://checkout.stripe.test/s'))
    expect(fetchMock).toHaveBeenCalledWith('/api/billing/checkout', expect.objectContaining({ method: 'POST' }))
  })

  it('offers the portal and no limits to a subscribed clinic, and warns about a failed payment', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(status({
      plan: 'Pro', status: 'PastDue', users: { used: 9 }, canUpgrade: false, canManage: true,
    }))))
    show()

    expect(await screen.findByText('Active users: 9')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('payment failed')
    expect(screen.getByRole('button', { name: 'Manage card and subscription' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Upgrade to Pro' })).not.toBeInTheDocument()
  })

  it('says the plan changes once Stripe confirms, not before', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(status())))
    show('/billing?checkout=success')

    expect(await screen.findByRole('status')).toHaveTextContent('as soon as Stripe confirms')
    expect(await screen.findByRole('heading', { name: 'Free plan' })).toBeInTheDocument()
  })

  it('shows no buttons when the server has no Stripe', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(status({ configured: false }))))
    show()

    expect(await screen.findByText('Billing is not set up on this server.')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})

describe('BillingBanner', () => {
  afterEach(() => vi.restoreAllMocks())

  const banner = (notice: object | null, canManage: boolean) => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(notice ? new Response(JSON.stringify(notice)) : new Response(null, { status: 204 }))
    return render(<MemoryRouter><BillingBanner canManage={canManage} /></MemoryRouter>)
  }

  it('gives the date and a way to fix it to an administrator', async () => {
    banner({ status: 'PastDue', until: '2026-11-12T12:00:00Z' }, true)

    expect(await screen.findByRole('note')).toHaveTextContent(`read-only on ${new Date('2026-11-12T12:00:00Z').toLocaleDateString()}`)
    expect(screen.getByRole('link', { name: 'Update the card' })).toHaveAttribute('href', '/billing')
  })

  it('tells others to ask their administrator, with no link they could not use', async () => {
    banner({ status: 'Suspended' }, false)

    expect(await screen.findByRole('note')).toHaveTextContent('tell your administrator')
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('offers a cancelled clinic to subscribe again', async () => {
    banner({ status: 'Cancelled', until: '2026-12-01T12:00:00Z' }, true)

    expect(await screen.findByRole('link', { name: 'Subscribe again' })).toBeInTheDocument()
  })

  it('shows nothing when there is nothing wrong', async () => {
    const { container } = banner(null, true)

    await new Promise(r => setTimeout(r, 0))
    expect(container).toBeEmptyDOMElement()
  })
})
