import ts from 'typescript'

// Inspect executable syntax, not comments or substrings such as "Consumes".
// Formatting elsewhere in a file must not exempt unrelated domain calculations.
export function viewModelDomainOperations(source: string): string[] {
  const tree = ts.createSourceFile('viewmodel.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const violations: string[] = []
  const visit = (node: ts.Node) => {
    if (ts.isCallExpression(node)) {
      const callee = node.expression
      const name = ts.isIdentifier(callee) ? callee.text
        : ts.isPropertyAccessExpression(callee) ? callee.name.text : ''
      if (/^(?:calculate|compute|validate|isValid|check[A-Z])|^(?:sum|average|reduce)$/.test(name)) {
        violations.push(`domain call: ${name}`)
      }
    }
    if (ts.isBinaryExpression(node)) {
      if ([ts.SyntaxKind.PlusToken, ts.SyntaxKind.MinusToken, ts.SyntaxKind.AsteriskToken,
        ts.SyntaxKind.SlashToken, ts.SyntaxKind.PercentToken].includes(node.operatorToken.kind)) {
        violations.push('inline calculation')
      }
      const statusOperand = (operand: ts.Expression) =>
        ts.isPropertyAccessExpression(operand) && operand.name.text === 'status'
      if (statusOperand(node.left) || statusOperand(node.right)) violations.push('status inference')
    }
    ts.forEachChild(node, visit)
  }
  visit(tree)
  return violations
}
