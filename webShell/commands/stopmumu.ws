关闭mumu模拟器
CMD
Short
chcp 65001
set "MUMU_MANAGER=C:\Program Files\Netease\MuMu Player 12\nx_main\MuMuManager.exe"
echo 关闭MuMu模拟器0号
"%MUMU_MANAGER%" control -v 0 shutdown
echo 已发送关机指令