*Disclaimer: This mod is made with ChatGPT 6.0 Astra and Opus 5.5*
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
  - Essential: Lower price range, more stable price, you can sell/buy alot of goods and price don't move too much, unless there are severe deficiet or overstock, profit is limited.
    - Grain, Rice, Meat, Common fishes, fruit, water belongs to this
  - Basic supply: Smaller stable price range, price will incease/drop faster than Essential price curve, got a higher profit margin than Essential.
    - Alcohol, Cheese, Orange, Special fish(Tuna, eels, northfish), Goods, Medicine, Nails, Salt, textile material belongs to here
  - Industrial goods: Liner price curve, predictable price change
    - Metals, tools, logs, lumber, sulfur, rubber belongs here
  - Luxury: Similar to vanilla price curve, overall most profitable, but price will drop sharply when the demand is not strong. Biggest price range.
    - Non essential food and item, gold, gems, silver
## Respondentia Loan (Cargo loan)
- Unlock at local rep level 1. According to your reputation, you will get credit limit and interest rate.
- Loan button added in trade book UI, you can buy trade goods without spending your money, instead spend your credit to get the cargo.
- Interest will be paid, along with the principal, when you sell the loaned cargo. There is no time limit when you need to sell the cargo.
- If your profit can cover both principal and interest, you will recive the local currency
- If your profit cannot cover all, the debit amount will be taken from the currency you loan.
- If you don't have enough money in loan currecny to pay back the debt, it will goes to negative money. While under debt you cannot do any action that will spend money until you make it positive again.
  - You will also receive reputation penalty, depends on your debt amount. You can only be reduce to rep 1 at minumum.
- Add bankruptcy button in trade book. It will reset your currency and reputation in that region to 0, all your debt will be clear at that region.
- Add Loan tab in Log menu. It record your current loan amount in each region, and each region's credit limit and interest rate.
  - Under loan tab there is also a bankruptcy button that will bankrupt you in all region. I'm not sure when you will need it but i will leave it here.
- Following condition will force you to payback loan right away in loaning currency
  - Cargo despawned from you leaving too far away
  - You open the sealed cargo
  - You sacrifice the cargo to the cat
- This system is used to replace the Reputation discount in vanilla. Since it don't work on trade book purchase anyway.
  - There is still retail discount (buying things from vendors), raise with your reputation.
## Cargo water damage
- Most of the cargo will now receive water damage when they are soaked in water, under rain, or drop into sea.
  - Trade goods that is not affect by water damage: 
    - coconuts, gems, iron, gold, copper, tools, sculptures, logs, nails, marble, silver, sulfur, rubber.
- Add water damage hint text for all water vulnerable trade good, when point at it will show current water damage level.
  - It start from 0%, progress to 100%.
  - This hint text can be toggle off in Configurator
- Cargo that receive water damage will receive price penalty. For mission cargo, it also affect reputation.
  - This effect start at 10% water damage, so you have some buffer.
  - Loan principal and interest won't get discount, pay up sucker
- Liquid cargo (All alcohol and water) also receive water damage, but won't get their price reduce until 100% water damage.
  - You can still drink 100% water damage alcohol and water, you will be fine.
- When water level in ship pass 5% of the cargo height, it will count as soaked and start damaging the cargo.
  - This means smaller cargo is more easily to get damage, also this means you can avoid this by elevated the cargo.
  - Water level over 5% don't increase damage rate. Soaking damage rate is fixed.
- After 10% of water damage, your cargo will start having water stain, that's your visual cue that your cargo start decreasing value.
- Water damage will also deepen your cargo's crate color.
- Wetted cargo can be dried. As long as it's water damage is below 75% (include 75%). It can be dried back up to 20%
  - 20% water damage will result in 90% value cargo (10% less than origin)
  - Liquid cargo cannot be dried. So don't let it reach 100%.
- Drying requires you to place cargo under open sky. Under full open sky and clear weather. A cargo can be dried from 75% to 20% in 24 in-game hour.
  - If the cargo is covered by something on top of it partially, it will still dry, but slower.
  - Fully covered cargo (either by other cargo, or by the ship structure) will not dry.
  - Cargo that have water touching it but below it's 5% height will also not dry.
  - Things without collision like canvas roof on dhow will be count as open sky.
- Rain will damage uncovered cargo. Partially covered cargo still get rain damage, but at slower speed.
  - If you are on small ship that don't have many hull structure to provide cover. Put things on top of cargo to cover it also work.
- The speed that cargo get damage is tied to rain intensity. Inside the storm and at the outskirt of storm will have diffent water damage rate.
- Water damage from rain and soaked water will stack.
### Wood plank for repairing
- Wood plank item is added so you have another way to counter the water damage.
## Trader, price infomation and tavern rumor change
## Currency mechanics change
## Vanilla mechanics changed
- Pushing cargo quantity over port capacity no longer stop production/consumption. Consumption will remain at max speed, and production will accerlate in deficit instead of stop producing.
- All price now calculate against port capacity, instead of fixed capacity of 100
- Game now only run market initialization once at new save, instead of everytime you load in game.
- Chronos and FFL special trade good can be bought anywhere, if any NPC trader bring it out.
