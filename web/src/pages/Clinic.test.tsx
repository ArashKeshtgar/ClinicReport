import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OnboardingChecklist } from './Clinic'

const steps = (done: string[]) => ({
  steps: ['email', 'letterhead', 'template', 'invite', 'report'].map(key => ({ key, title: `Step ${key}`, done: done.includes(key) })),
  complete: done.length === 5,
})

describe('OnboardingChecklist', () => {
  afterEach(() => vi.restoreAllMocks())

  it('links the steps still to do and counts the done ones', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(steps(['email', 'letterhead']))))
    render(<MemoryRouter><OnboardingChecklist /></MemoryRouter>)

    expect(await screen.findByText('2 of 5 done')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Step template' })).toHaveAttribute('href', '/templates')
    expect(screen.getByRole('link', { name: 'Step report' })).toHaveAttribute('href', '/reports/new')
    expect(screen.queryByRole('link', { name: 'Step letterhead' })).not.toBeInTheDocument()
  })

  it('is gone once every step is done', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(steps(['email', 'letterhead', 'template', 'invite', 'report']))))
    const { container } = render(<MemoryRouter><OnboardingChecklist /></MemoryRouter>)

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalled())
    await new Promise(r => setTimeout(r, 0))
    expect(container).toBeEmptyDOMElement()
  })
})
