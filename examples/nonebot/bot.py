import nonebot
from nonebot.adapters.milky import Adapter

nonebot.init()
nonebot.get_driver().register_adapter(Adapter)
nonebot.load_plugin("plugins.nyarugram_demo")
nonebot.run()
