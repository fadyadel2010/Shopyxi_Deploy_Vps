import { expectTypeOf, it } from 'vitest';
import type { affiliateApi } from './affiliateApi';
import type { affiliateAxios } from './affiliateAuthApi';

it('preserves the affiliate transport request options without a direct transport-library import', () => {
    type TransportConfig = NonNullable<Parameters<typeof affiliateAxios.get>[1]>;
    expectTypeOf<NonNullable<Parameters<typeof affiliateApi.getProductIntelligence>[1]>>().toEqualTypeOf<TransportConfig>();
    expectTypeOf<NonNullable<Parameters<typeof affiliateApi.listPromotionLinks>[0]>>().toEqualTypeOf<TransportConfig>();
});
