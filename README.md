*Disclaimer: This mod is made with ChatGPT 6.0 Astra*
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
    - Stpales, meat, common fishes, water belongs to this
  - Basic supply: Smaller stable price range, price will incease/drop faster than Essential price curve, got a higher profit margin than Essential.
    - Alcohol, Cheese,Fruit, Special fish(Tuna, eels, northfish), Goods, Medicine, Nails, Salt, textile material belongs to here
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
- This system is used to replace the Reputation discount in vanilla. Since it don't work on trade book purchase anyway.
  - There is still retail discount (buying things from vendors), raise with your reputation.
## Cargo water damage


## Trader, price infomation and tavern rumor change

## Vanilla mechanics fix
- Pushing cargo quantity over port capacity no longer stop production/consumption. Consumption will remain at max speed, and production will accerlate in deficit instead of stop producing.
