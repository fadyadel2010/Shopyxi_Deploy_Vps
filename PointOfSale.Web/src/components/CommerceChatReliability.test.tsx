import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import ChatWidget from '../../../PointOfSale.Storefront/src/components/chat/ChatWidget'

const state = vi.hoisted(() => ({
    enabled: true, config: { version: 1 }, customerId: 'customer-A', post: vi.fn(),
    i18n: { language: 'en' },
}))
vi.mock('@/i18n', () => ({ useTranslation: () => ({ t: (key: string) => key, language: state.i18n.language }) }))
vi.mock('@/store', () => ({
    useStoreConfigStore: (selector: (s: unknown) => unknown) => selector({ config: state.config }),
    selectCommerceAiEnabled: () => state.enabled,
    selectCommerceAiWidgetStyle: () => 'minimal',
    selectCommerceAiWidgetPosition: () => 'bottom-right',
    selectCommerceAiAutoGreetingEnabled: () => false,
    selectCommerceAiAutoGreetingMessage: () => '',
}))
vi.mock('@/auth/store/authStore', () => ({ useAuthStore: (selector: (s: unknown) => unknown) => selector({ customer: { id: state.customerId } }) }))
vi.mock('@/bootstrap/tenantScopedStorage', () => ({ tryGetTenantScopedStorageKey: (key: string) => `tenant-A:${key}` }))
vi.mock('@/bootstrap/sessionManager', () => ({ getSessionId: () => 'cart-session' }))
vi.mock('@/services/api/httpClient', () => ({ httpClient: { post: (...args: unknown[]) => state.post(...args) } }))
vi.mock('../../../PointOfSale.Storefront/src/components/chat/OrderDraftPreview', () => ({ default: () => <div>Trusted purchase preview</div> }))

beforeEach(() => {
    state.enabled = true; state.config = { version: 1 }; state.customerId = 'customer-A'; state.i18n.language = 'en'
    state.post.mockReset()
    state.post.mockImplementation((url: string) => url.includes('/event') ? Promise.resolve({ status: 202 }) : Promise.resolve({ data: { type: 'message', message: 'Trusted reply' } }))
    Element.prototype.scrollIntoView = vi.fn()
})
function open() { fireEvent.click(screen.getByRole('button', { name: 'Open chat' })) }
function send(text = 'مرحبا') {
    fireEvent.change(screen.getByPlaceholderText('Type your message...'), { target: { value: text } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
}

it('submits Arabic and English messages and carries bounded conversation history', async () => {
    render(<ChatWidget />); open(); send()
    expect(await screen.findByText('Trusted reply')).toBeInTheDocument()
    send('Tell me about products')
    await waitFor(() => expect(state.post.mock.calls.filter(c => c[0] === '/api/storefront/chat')).toHaveLength(2))
    const call = state.post.mock.calls.filter(c => c[0] === '/api/storefront/chat')[1]
    expect(call[1]).toMatchObject({ message: 'Tell me about products', language: 'en', sessionId: 'cart-session', history: [{ role: 'user', content: 'مرحبا' }, { role: 'assistant', content: 'Trusted reply' }] })
    expect(call[2]).toMatchObject({ timeout: 125000 })
})

it('shows loading, prevents duplicate requests, then renders the successful reply', async () => {
    let resolve!: (value: unknown) => void
    state.post.mockImplementation((url: string) => url.includes('/event') ? Promise.resolve({ status: 202 }) : new Promise(r => { resolve = r }))
    render(<ChatWidget />); open(); send('hello')
    expect(screen.getByPlaceholderText('Type your message...')).toBeDisabled()
    await act(async () => resolve({ data: { type: 'message', message: 'Recovered' } }))
    expect(await screen.findByText('Recovered')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Type your message...')).toBeEnabled()
})

it.each([null, {}, { type: 'message', message: '' }, { type: 'invalid', message: 'invalid' },
    { type: 'message', message: 'Broken', missingInfo: {} }, { type: 'order_draft', message: 'Broken', orderDraft: { intent: 'create', items: null } }])('rejects malformed responses and allows explicit retry', async data => {
    state.post.mockImplementation((url: string) => url.includes('/event') ? Promise.resolve({ status: 202 }) : Promise.resolve({ data }))
    render(<ChatWidget />); open(); send('hello')
    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to send message')
    state.post.mockImplementation(() => Promise.resolve({ data: { type: 'message', message: 'Recovered' } }))
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText('Recovered')).toBeInTheDocument()
})

it('does not render or send when disabled', () => {
    state.enabled = false; const { container } = render(<ChatWidget />)
    expect(container).toBeEmptyDOMElement(); expect(state.post).not.toHaveBeenCalled()
})

it('clears conversation on customer authority change', async () => {
    const view = render(<ChatWidget />); open(); send('private question')
    await screen.findByText('Trusted reply')
    state.customerId = 'customer-B'; view.rerender(<ChatWidget />); open()
    expect(screen.queryByText('private question')).not.toBeInTheDocument()
})
