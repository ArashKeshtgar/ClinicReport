import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Billing from './Billing'

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
