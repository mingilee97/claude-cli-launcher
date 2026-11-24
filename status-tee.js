// Claude 런처용 상태줄: 받은 상태 정보를 런처에 넘기고, 터미널에는 아무것도 그리지 않는다.
const fs = require("fs"), path = require("path");
let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (c) => (raw += c));
process.stdin.on("end", () => {
  try {
    const d = JSON.parse(raw);
    d._at = Date.now();
    const dir = path.join(__dirname, "status");
    fs.mkdirSync(dir, { recursive: true });
    const key = (process.env.CLAUDE_LAUNCHER_TAB || "x").replace(/[^\w-]/g, "");
    const file = path.join(dir, key + ".json");
    fs.writeFileSync(file + ".tmp", JSON.stringify(d));
    fs.renameSync(file + ".tmp", file);
    // 원래 상태줄과 같은 한도 기억 파일을 함께 고친다.
    const rl = d.rate_limits;
    if (rl && (rl.five_hour || rl.seven_day)) {
      const claudeHome = process.env.CLAUDE_CONFIG_DIR || path.join(process.env.USERPROFILE || process.env.HOME || "", ".claude");
      const rlDir = path.join(claudeHome, "mods", "statusline");
      fs.mkdirSync(rlDir, { recursive: true });
      fs.writeFileSync(path.join(rlDir, "statusline-ratelimits.json"), JSON.stringify(rl));
    }
  } catch {}
  process.stdout.write("");
});
