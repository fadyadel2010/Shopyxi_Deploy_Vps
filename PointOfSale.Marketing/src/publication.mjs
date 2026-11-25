import policy from './content/publication.json' with { type: 'json' };

export function publicClaim(claims, id, locale) {
  if (!policy.supported.includes(id) || policy.excluded.includes(id)) {
    throw Error(`Claim ${id} is not supported for public publication`);
  }
  return policy.overrides[id]?.[locale] ?? claims[id][locale];
}
