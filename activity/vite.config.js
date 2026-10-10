// Built into the bot's web root, served at /activity/ (Discord maps the Activity's root there).
export default {
  base: "./",
  build: { outDir: "../src/wwwroot/activity", emptyOutDir: true },
};
