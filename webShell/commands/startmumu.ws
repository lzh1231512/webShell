启动mumu模拟器
CMD
Short
chcp 65001
set "MUMU_MANAGER=C:\Program Files\Netease\MuMu Player 12\nx_main\MuMuManager.exe"
echo 正在启动MuMu模拟器0号实例
"%MUMU_MANAGER%" control -v 0 launch
timeout /t 5 /nobreak >nul

echo 执行hide_window隐藏模拟器窗口
"%MUMU_MANAGER%" control -v 0 hide_window
timeout /t 2 /nobreak >nul

echo 设置模拟器静音
"%MUMU_MANAGER%" control -v 0 tool func -n volume_mute
timeout /t 2 /nobreak >nul

echo 启动游戏
"%MUMU_MANAGER%" control -v 0 app launch -pkg com.xjskp.tv.hnsc
timeout /t 20 /nobreak >nul



echo.
echo ======================================
echo 模拟器已后台隐藏完成。
echo ======================================