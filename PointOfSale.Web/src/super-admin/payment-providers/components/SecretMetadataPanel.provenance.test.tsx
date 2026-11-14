import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SecretMetadataPanel } from './SecretMetadataPanel'
import { SecretVersionList } from './SecretVersionList'
import type { PaymentProviderSecretMetadata } from '../paymentProviderTypes'

const legacy: PaymentProviderSecretMetadata = {
    secretId: 'synthetic-metadata-id', providerCode: 'TEST', secretType: 'API Key', version: 1,
    createdAtUtc: null, activatedAtUtc: null, retiredAtUtc: null,
    createdBy: 'Legacy (provenance not recorded)', status: 'Active', fingerprint: 'TEST-METADATA',
    lastRotatedAtUtc: null, lastUsedAtUtc: null,
}

describe('missing historical credential provenance', () => {
    it('never represents unknown dates as future events', () => {
        render(<><SecretMetadataPanel secret={legacy} /><SecretVersionList versions={[legacy]} /></>)
        expect(screen.getAllByText('Not recorded')).toHaveLength(6)
        expect(screen.getByText('Legacy (provenance not recorded)')).toBeInTheDocument()
        expect(screen.queryByText('Future')).not.toBeInTheDocument()
    })
    it('renders recorded past and future dates without replacing their meaning', () => {
        render(<SecretVersionList versions={[{ ...legacy, createdAtUtc: '2020-01-02T00:00:00Z', activatedAtUtc: '2099-01-02T00:00:00Z' }]} />)
        expect(screen.getByText(/2020/)).toBeInTheDocument()
        expect(screen.getByText(/2099/)).toBeInTheDocument()
        expect(screen.getAllByText('Not recorded')).toHaveLength(1)
    })
})
