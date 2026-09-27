## Port Capacity and Port production
- All ports is separate into 4 tiers. Each tier have differenct port capacity, which affect their cargo hold capacity, production speed and demands.
- Bigger the Port Capacity, more trade goods will be available, production/consumption will be faster.
- Tiers assigned as following:
  - T1 Port (200 capacity): Gold Rock City, Fort Aestrin, Dragon Cliffs
  - T2 Port (150 capacity): Oasis, Happy Bay, Kicia Bay, Chronos
  - T3 Port (100 capacity): All other port
  - T4 Port (50 capacity): Al'ankh Academy, Alchemist Island, Albacore Town, Aestra Abbey, Sanctuary, On'na
  - For reference, Vanilla 3 main cities is at 100 capacity, all other port is 50 capacity)
- Cargo Mission will now increase the port capacity. Also the cargo from mission will now fulfill the port's demand at half the rate(so 2 units of cargo fulfill 1 units of demand), thus it will move the price.
  - Local mission increase departure port by 2, arrival port by 1.
  - World mission increase departure port by 6, arrival port by 3.
- Background trader would also contribute to port capacity, thus every port will grow in capacity overtime.
- All port got their production/demand value redo. Production/consumption is overall much MUCH faster than vanilla. There will be more available goods around, and port consume trade good faster.
## Trade good category, distribution and price change
- All trade good's produce number is redistribute. This mod try to stay as close to vanilla as possible, keep the regional/port special trade good identity.
- Trade goods are sepearated into different category, which decide their consumption numbers for each tier of port.
  - For example: Grains and Rice are Staples. It have very big demands everywhere. On the other hand Silk belongs to luxury goods, the demands will be biggest in T1 and T2 city. T3 and T4 city will care less.
- Add production chain logic to increase certain demand for port that produce certain goods.
  - For example: Tools need iron, copper, lumber and rubber. Thus Tool producing port will have increased need for these things. Sculpture need marble, wine need sulfur, sausage need meats, meats need salt...etc.
- There is now 4 price curve that decide goods price: Essential, Basic supply, Industrial goods, Luxury
  - Essential:
## Vanilla mechanics fix
- Pushing cargo quantity over port capacity no longer stop production/consumption. Consumption will remain at max speed, and production will accerlate in deficit instead of stop producing.
