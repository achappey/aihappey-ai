---
name: Provider maintenance
on:
  schedule:
    - cron: "0 8,14,20 * * *"
      timezone: Europe/Amsterdam
  workflow_dispatch:
permissions:
  contents: read
  copilot-requests: none
concurrency:
  group: provider-maintenance
  cancel-in-progress: false
engine:
  id: copilot
  env:
    COPILOT_GITHUB_TOKEN: ${{ secrets.GH_AW_AGENT_TOKEN }}
tools:
  github: false
safe-outputs:
  create-agent-session:
    max: 1
    github-token: ${{ secrets.GH_AW_AGENT_TOKEN }}
  steps:
    - name: Check maintenance work and supply the complete trusted prompt
      uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
      env:
        GH_AW_AGENT_OUTPUT: ${{ steps.setup-agent-output-env.outputs.GH_AW_AGENT_OUTPUT }}
      with:
        github-token: ${{ secrets.GH_AW_AGENT_TOKEN }}
        script: |
          const fs = require('node:fs');
          const outputPath = process.env.GH_AW_AGENT_OUTPUT;
          const marker = '[provider-maintenance]';
          const headers = { 'X-GitHub-Api-Version': '2026-03-10' };
          for (let page = 1; ; page++) {
            const { data: taskPage } = await github.request('GET /agents/repos/{owner}/{repo}/tasks', {
              ...context.repo, per_page: 100, page, headers,
              state: 'queued,in_progress,idle,waiting_for_user'
            });
            if (!Array.isArray(taskPage.tasks)) throw new Error('Invalid task listing');
            for (const task of taskPage.tasks) {
              const { data: detail } = await github.request('GET /agents/repos/{owner}/{repo}/tasks/{task_id}', {
                ...context.repo, task_id: task.id, headers
              });
              // Task names can change; use session prompts and fail closed if unavailable.
              if (!Array.isArray(detail.sessions) || !detail.sessions.length ||
                detail.sessions.some(session => typeof session.prompt !== 'string' ||
                  !session.prompt.trim() || session.prompt.includes(marker))) {
                fs.writeFileSync(outputPath, JSON.stringify({ items: [] }));
                core.notice('Skipping: an active maintenance task or unidentifiable cloud task exists.');
                return;
              }
            }
            if (taskPage.tasks.length < 100) break;
          }
          const { data } = await github.rest.repos.getContent({
            ...context.repo, path: '.github/prompts/provider-maintenance.md',
            ref: context.sha
          });
          const prompt = Buffer.from(data.content, 'base64').toString('utf8');
          if (!prompt.trim()) throw new Error('Maintenance prompt is empty');
          const output = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
          const sessions = output.items.filter(item => item.type === 'create_agent_session');
          if (sessions.length !== 1) throw new Error('Expected exactly one agent session request');
          const body = marker + '\n\n' + prompt +
            '\n\nIf changes are warranted, open a pull request with ' + marker +
            ' in its title and body. Never merge automatically or enable auto-merge.';
          fs.writeFileSync(outputPath, JSON.stringify({
            items: [{ type: 'create_agent_session', body }]
          }));
---

# Provider maintenance dispatcher

Request exactly one `create_agent_session` safe output with body
`Provider maintenance; the trusted safe-output step supplies the full prompt.`

Do not inspect or modify providers, run maintenance, or create a pull request here.
The separate Copilot cloud agent session performs all maintenance. The trusted
safe-output step checks for existing work and reads the complete maintenance
prompt directly from `.github/prompts/provider-maintenance.md` at this run's
commit, without summarizing or rewriting it.

Configuration: enable Copilot cloud agent access (the task API requires Copilot
Business or Enterprise). Set `GH_AW_AGENT_TOKEN` to a fine-grained PAT with this
repository selected, Agent tasks read/write, Contents and Pull requests read,
and account-level Copilot Requests access for the dispatcher. Launch guards
fail closed on API errors or unavailable session prompts, block active marked
maintenance tasks, and allow identifiable unrelated tasks. Open PRs do not block
launches; the maintenance agent checks PRs and issues before selecting work.

Recompile with `gh aw compile provider-maintenance --validate --shellcheck`
using gh-aw v0.92.0. Commit the source and generated `.lock.yml` together.
