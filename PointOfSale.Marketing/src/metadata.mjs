// All substantive descriptions reuse exact registered claims, including qualifications.
export const descriptionClaims = {
  home: 'C01', platform: 'C06', pos: 'C07', storefront: 'C17', affiliate: 'C04', commercehub: 'C05'
};
export function metadata(route, claims, labels) {
  return {
    title: `${labels[route.locale][route.page]} — SHOPYXI`,
    description: claims[descriptionClaims[route.page]][route.locale],
    claim: descriptionClaims[route.page]
  };
}
