const e = (o, n) => {
  console.log("Hello from my extension 🎉");
}, s = (o, n) => {
  console.log("Goodbye from my extension 👋");
};
export {
  e as onInit,
  s as onUnload
};
