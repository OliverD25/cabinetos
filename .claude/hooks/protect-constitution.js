// PreToolUse hook: any change to CONSTITUTION.md needs the creator's approval.
// The permission rule in settings.json alone did not stop an edit in auto mode,
// so this hook forces the decision back to a human prompt.

const PROTECTED = "CONSTITUTION.md";

const SHELL_WRITE = new RegExp(
  [
    String.raw`\bsed\b[^|;&]*\s-i`,
    String.raw`>>?\s*["']?[^\s;|&]*CONSTITUTION\.md`,
    String.raw`\btee\b`,
    String.raw`\b(mv|cp|rm|truncate|dd|perl|python3?|node)\b`,
    String.raw`\bgit\s+(checkout|restore|rm|mv|reset|stash|apply|am|cherry-pick|revert)\b`,
    String.raw`\b(Set-Content|Add-Content|Out-File|Remove-Item|Move-Item|Copy-Item|Clear-Content)\b`,
  ].join("|"),
  "i",
);

function targetsConstitution(toolName, input) {
  const path = input.file_path || input.notebook_path || "";
  if (path) return path.replace(/\\/g, "/").split("/").pop() === PROTECTED;
  const command = input.command || "";
  if (!command.includes(PROTECTED)) return false;
  return SHELL_WRITE.test(command);
}

let raw = "";
process.stdin.on("data", (chunk) => (raw += chunk));
process.stdin.on("end", () => {
  let event;
  try {
    event = JSON.parse(raw);
  } catch {
    process.exit(0);
  }
  if (!targetsConstitution(event.tool_name, event.tool_input || {})) process.exit(0);

  process.stdout.write(
    JSON.stringify({
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "ask",
        permissionDecisionReason:
          "CONSTITUTION.md is protected. Approve only if the creator approved this exact change in chat.",
      },
    }),
  );
});
